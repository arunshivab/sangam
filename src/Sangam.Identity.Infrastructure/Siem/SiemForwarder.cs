using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Sangam.Identity.Application.Abstractions;
using Sangam.Identity.Application.Monitoring;
using Sangam.Identity.Domain.Entities;
using Sangam.Identity.Infrastructure.Persistence;

namespace Sangam.Identity.Infrastructure.Siem;

/// <summary>
/// PR-32 (CAP-084): sends the audit log to a SIEM as it grows. One host at a time (a PostgreSQL advisory lock); the
/// position reached is kept with the status in <c>host_reports</c> (host <c>siem</c>), so a restart resumes where it
/// stopped. Delivery is at least once: a crash between sending and recording the position sends those events again,
/// and the receiver drops repeats by the audit id. When the receiver cannot be reached, nothing is lost: the events stay
/// in the audit log and are sent when it answers again. The monitoring page shows the position, the lag and the last
/// error.
/// </summary>
public sealed partial class SiemForwarder : IAsyncDisposable
{
    /// <summary>PostgreSQL advisory-lock key that keeps streaming to one host at a time.</summary>
    public const long LockKey = 0x5A6E_6761_5369_656D;

    /// <summary>The <c>host_reports</c> host and subject that hold the status and position.</summary>
    public const string ReportHost = "siem";

    /// <summary>The <c>host_reports</c> subject.</summary>
    public const string ReportSubject = "siem_stream";

    private readonly IServiceScopeFactory _scopes;
    private readonly SiemOptions _options;
    private readonly IClock _clock;
    private readonly string _environment;
    private readonly ILogger<SiemForwarder> _logger;
    private TcpClient? _tcp;
    private Stream? _stream;

    /// <summary>Initialises the forwarder.</summary>
    /// <param name="scopes">Scope factory.</param>
    /// <param name="options">Settings.</param>
    /// <param name="clock">Clock.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="environment">Host environment; Production when the container has none (a tool or a test).</param>
    public SiemForwarder(IServiceScopeFactory scopes, SiemOptions options, IClock clock, ILogger<SiemForwarder> logger, IHostEnvironment? environment = null)
    {
        _scopes = scopes ?? throw new ArgumentNullException(nameof(scopes));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _environment = environment?.EnvironmentName ?? "Production";
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Sends whatever is new, in batches, until nothing is left or the receiver fails.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>How many events were sent.</returns>
    public async Task<int> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
        {
            return 0;
        }

        using IServiceScope scope = _scopes.CreateScope();
        SangamDbContext db = scope.ServiceProvider.GetRequiredService<SangamDbContext>();
        await db.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            bool mine = await db.Database.SqlQueryRaw<bool>("SELECT pg_try_advisory_lock(@key) AS \"Value\"", new NpgsqlParameter("key", LockKey))
                .SingleAsync(cancellationToken).ConfigureAwait(false);
            if (!mine)
            {
                return 0;
            }

            try
            {
                return await SendAsync(db, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_unlock(@key)", [new NpgsqlParameter("key", LockKey)], CancellationToken.None).ConfigureAwait(false);
            }
        }
        finally
        {
            await db.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }

    /// <summary>The last status recorded, or <see langword="null"/> before the first round.</summary>
    /// <param name="db">Database.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static async Task<SiemStatus?> ReadStatusAsync(SangamDbContext db, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        string? payload = await db.HostReports.AsNoTracking()
            .Where(r => r.Host == ReportHost && r.Subject == ReportSubject)
            .Select(r => r.Payload)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return payload is null ? null : JsonSerializer.Deserialize<SiemStatus>(payload);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await CloseAsync().ConfigureAwait(false);
    }

    private async Task<int> SendAsync(SangamDbContext db, CancellationToken cancellationToken)
    {
        SiemStatus? status = await ReadStatusAsync(db, cancellationToken).ConfigureAwait(false);
        long position;
        if (status is not null)
        {
            position = status.LastId;
        }
        else
        {
            // A first start: from now on, or from the oldest event still live.
            long newest = await db.AuditEvents.AsNoTracking().OrderByDescending(e => e.Id).Select(e => e.Id).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            position = _options.StartFrom == "beginning" ? 0 : newest;
        }

        long sentBefore = status?.Sent ?? 0;
        int sent = 0;
        string? error = null;
        while (!cancellationToken.IsCancellationRequested)
        {
            List<AuditEvent> batch = await db.AuditEvents.AsNoTracking()
                .Where(e => e.Id > position)
                .OrderBy(e => e.Id)
                .Take(Math.Clamp(_options.BatchSize, 1, 5000))
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            if (batch.Count == 0)
            {
                break;
            }

            try
            {
                Stream stream = await ConnectAsync(cancellationToken).ConfigureAwait(false);
                foreach (AuditEvent e in batch)
                {
                    await stream.WriteAsync(SiemFormat.Frame(_options.Format, e, Environment.MachineName, _environment), cancellationToken).ConfigureAwait(false);
                }

                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or SocketException or AuthenticationException or ObjectDisposedException or InvalidOperationException)
            {
                error = ex.GetType().Name + ": " + ex.Message;
                LogSendFailed(_options.Host, _options.Port, error);
                await CloseAsync().ConfigureAwait(false);
                break;
            }

            position = batch[^1].Id;
            sent += batch.Count;
            await ReportAsync(db, position, sentBefore + sent, null, status, cancellationToken).ConfigureAwait(false);
        }

        if (sent == 0 || error is not null)
        {
            await ReportAsync(db, position, sentBefore + sent, error, status, cancellationToken).ConfigureAwait(false);
        }

        return sent;
    }

    private async Task ReportAsync(SangamDbContext db, long position, long total, string? error, SiemStatus? previous, CancellationToken cancellationToken)
    {
        DateTimeOffset now = _clock.UtcNow;
        long newest = await db.AuditEvents.AsNoTracking().OrderByDescending(e => e.Id).Select(e => e.Id).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        SiemStatus status = new(
            LastId: position,
            Sent: total,
            Lag: Math.Max(0, newest - position),
            Receiver: _options.Host + ":" + _options.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Format: _options.Format,
            Tls: _options.Tls,
            CheckedAt: now,
            LastSentAt: total > (previous?.Sent ?? 0) ? now : previous?.LastSentAt,
            LastError: error,
            LastErrorAt: error is null ? previous?.LastErrorAt : now);
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO host_reports (host, subject, payload, reported_at) VALUES (@host, @subject, @payload, @at) " +
            "ON CONFLICT (host, subject) DO UPDATE SET payload = excluded.payload, reported_at = excluded.reported_at",
            [
                new NpgsqlParameter("host", ReportHost),
                new NpgsqlParameter("subject", ReportSubject),
                new NpgsqlParameter("payload", JsonSerializer.Serialize(status)),
                new NpgsqlParameter("at", now),
            ],
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<Stream> ConnectAsync(CancellationToken cancellationToken)
    {
        if (_stream is not null && _tcp is { Connected: true })
        {
            return _stream;
        }

        await CloseAsync().ConfigureAwait(false);
        TcpClient tcp = new() { NoDelay = true };
        try
        {
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            await tcp.ConnectAsync(_options.Host, _options.Port, timeout.Token).ConfigureAwait(false);
            if (!_options.Tls)
            {
                _tcp = tcp;
                _stream = tcp.GetStream();
                return _stream;
            }

            SslStream ssl = new(tcp.GetStream(), leaveInnerStreamOpen: false);
            X509Certificate2? authority = string.IsNullOrWhiteSpace(_options.CaCertificatePath) ? null : X509CertificateLoader.LoadCertificateFromFile(_options.CaCertificatePath);
            X509Certificate2Collection clients = [];
            if (!string.IsNullOrWhiteSpace(_options.ClientCertificatePath))
            {
                clients.Add(X509CertificateLoader.LoadPkcs12FromFile(_options.ClientCertificatePath, _options.ClientCertificatePassword));
            }

            try
            {
                await ssl.AuthenticateAsClientAsync(
                    new SslClientAuthenticationOptions
                    {
                        TargetHost = string.IsNullOrWhiteSpace(_options.ServerName) ? _options.Host : _options.ServerName,
                        EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                        ClientCertificates = clients,
                        CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                        RemoteCertificateValidationCallback = (_, certificate, _, errors) => Trusted(certificate, errors, authority),
                    },
                    timeout.Token).ConfigureAwait(false);
            }
            catch
            {
                await ssl.DisposeAsync().ConfigureAwait(false);
                throw;
            }
            finally
            {
                authority?.Dispose();
            }

            _tcp = tcp;
            _stream = ssl;
            return ssl;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            tcp.Dispose();
            throw new IOException($"The SIEM receiver {_options.Host}:{_options.Port} did not answer within 10 seconds.");
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    /// <summary>Accepts the receiver's certificate: publicly trusted, or — when an authority is configured — issued by
    /// that authority only. The name must always match.</summary>
    private static bool Trusted(X509Certificate? certificate, SslPolicyErrors errors, X509Certificate2? authority)
    {
        if (authority is null)
        {
            return errors == SslPolicyErrors.None;
        }

        if (certificate is null || (errors & ~SslPolicyErrors.RemoteCertificateChainErrors) != SslPolicyErrors.None)
        {
            return false;
        }

        using X509Certificate2 leaf = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
        using X509Chain chain = new();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(authority);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        return chain.Build(leaf);
    }

    private async Task CloseAsync()
    {
        if (_stream is not null)
        {
            await _stream.DisposeAsync().ConfigureAwait(false);
            _stream = null;
        }

        _tcp?.Dispose();
        _tcp = null;
    }

    [LoggerMessage(EventId = 1801, Level = LogLevel.Warning, Message = "Could not send audit events to the SIEM at {Host}:{Port}: {Error}. They stay in the audit log and are sent when it answers again.")]
    private partial void LogSendFailed(string host, int port, string error);
}

/// <summary>Runs <see cref="SiemForwarder"/> every <see cref="SiemOptions.Interval"/> while streaming is on.</summary>
public sealed partial class SiemStreamingService : BackgroundService
{
    private readonly SiemForwarder _forwarder;
    private readonly SiemOptions _options;
    private readonly ILogger<SiemStreamingService> _logger;

    /// <summary>Initialises the service.</summary>
    /// <param name="forwarder">The forwarder.</param>
    /// <param name="options">Settings.</param>
    /// <param name="logger">Logger.</param>
    public SiemStreamingService(SiemForwarder forwarder, SiemOptions options, ILogger<SiemStreamingService> logger)
    {
        _forwarder = forwarder ?? throw new ArgumentNullException(nameof(forwarder));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            return;
        }

        LogStarted(_options.Host, _options.Port, _options.Format, _options.Tls);
        using PeriodicTimer timer = new(_options.Interval < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : _options.Interval);
        do
        {
            try
            {
                await _forwarder.RunOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogRoundFailed(ex);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    [LoggerMessage(EventId = 1800, Level = LogLevel.Information, Message = "Streaming audit events to the SIEM at {Host}:{Port} ({Format}, TLS {Tls}).")]
    private partial void LogStarted(string host, int port, string format, bool tls);

    [LoggerMessage(EventId = 1802, Level = LogLevel.Error, Message = "A SIEM streaming round failed.")]
    private partial void LogRoundFailed(Exception ex);
}
