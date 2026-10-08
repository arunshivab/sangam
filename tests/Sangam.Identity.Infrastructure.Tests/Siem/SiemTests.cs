using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Sangam.Client.Audit;
using Sangam.Identity.Application.Abstractions;
using Sangam.Identity.Application.Monitoring;
using Sangam.Identity.Domain;
using Sangam.Identity.Domain.Entities;
using Sangam.Identity.Domain.Enums;
using Sangam.Identity.Infrastructure.Persistence;
using Sangam.Identity.Infrastructure.Services;
using Sangam.Identity.Infrastructure.Siem;
using Sangam.Identity.Infrastructure.Tests.Postgres;

namespace Sangam.Identity.Infrastructure.Tests.Siem;

/// <summary>
/// PR-32 (CAP-084): the audit log streamed to a SIEM over TLS, as CEF in syslog or as JSON in the shared audit schema;
/// off by default, plain TCP refused in Production, resumed after a restart, and nothing lost while the receiver is
/// down.
/// </summary>
[Collection("postgres")]
public sealed partial class SiemTests : IAsyncLifetime
{
    private static readonly FieldInfoSet AllActions = new();
    private readonly PostgresFixture _pg;
    private readonly string _dir = Directory.CreateTempSubdirectory("sangam-siem-").FullName;

    public SiemTests(PostgresFixture pg)
    {
        _pg = pg;
    }

    public async Task InitializeAsync()
    {
        if (_pg.IsAvailable)
        {
            await _pg.ResetAsync();
            await using SangamDbContext db = _pg.CreateContext();
            await db.HostReports.Where(r => r.Host == SiemForwarder.ReportHost).ExecuteDeleteAsync();
        }
    }

    public Task DisposeAsync()
    {
        Directory.Delete(_dir, recursive: true);
        return Task.CompletedTask;
    }

    [Theory]
    [InlineData("Production", null, null, null, null)]
    [InlineData("Production", "true", "", null, "without a receiver")]
    [InlineData("Production", "true", "siem.example.in", "false", "plain TCP is not allowed")]
    [InlineData("Development", "true", "localhost", "false", null)]
    [InlineData("Production", "true", "siem.example.in", "true", null)]
    public void TheSettings_AreRefusedAtStart_WhenUnsafe(string environment, string? enabled, string? host, string? tls, string? refusal)
    {
        Dictionary<string, string?> settings = new()
        {
            ["Sangam:Siem:Enabled"] = enabled,
            ["Sangam:Siem:Host"] = host,
            ["Sangam:Siem:Tls"] = tls,
        };
        string? problem = SiemOptions.Validate(environment, new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
        if (refusal is null)
        {
            Assert.Null(problem);
        }
        else
        {
            Assert.Contains(refusal, problem, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void StreamingIsOff_UnlessSwitchedOn()
    {
        SiemOptions options = SiemOptions.From(new ConfigurationBuilder().Build());
        Assert.False(options.Enabled);
        Assert.True(options.Tls);
        Assert.Equal(6514, options.Port);
        Assert.Equal("cef", options.Format);
    }

    [Fact]
    public void ACefRecord_IsSyslog_FacilityLogAudit_WithItsSpecialCharactersEscaped()
    {
        AuditEvent failed = Event(42, AuditActions.UserLoginFail, "{\"reason\":\"a=b\\nc|d\"}");
        string syslog = SiemFormat.Syslog(failed, "id-1");

        Assert.StartsWith("<108>1 ", syslog, StringComparison.Ordinal);
        Assert.Contains(" id-1 sangam - audit - CEF:0|imagiQa|Sangam|", syslog, StringComparison.Ordinal);
        Assert.Contains("|user.login.fail|user.login.fail|6|", syslog, StringComparison.Ordinal);
        Assert.Contains("externalId=42 ", syslog, StringComparison.Ordinal);
        Assert.Contains("outcome=failure ", syslog, StringComparison.Ordinal);
        Assert.Contains("src=203.0.113.9 ", syslog, StringComparison.Ordinal);
        Assert.Contains("cs6={\"reason\":\"a\\=b\\\\nc|d\"}", syslog, StringComparison.Ordinal);
        Assert.DoesNotContain('\n', syslog);
        Assert.StartsWith("<110>1 ", SiemFormat.Syslog(Event(1, AuditActions.UserLoginSuccess), "id-1"), StringComparison.Ordinal);

        byte[] framed = SiemFormat.Frame("cef", failed, "id-1", "Production");
        string text = Encoding.UTF8.GetString(framed);
        int space = text.IndexOf(' ', StringComparison.Ordinal);
        Assert.Equal(Encoding.UTF8.GetByteCount(text[(space + 1)..]), int.Parse(text[..space], System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void EveryIdentityAction_InTheJsonFormat_IsAValidSharedAuditEvent()
    {
        long id = 1;
        foreach (string action in AllActions.Values)
        {
            foreach (AuditActorType actor in Enum.GetValues<AuditActorType>())
            {
                AuditEvent e = Event(id++, action, actor: actor, ip: actor is AuditActorType.User ? null : "198.51.100.4");
                JsonObject envelope = SiemFormat.Json(e, "Production");
                IReadOnlyList<string> problems = SangamAudit.Validate(envelope["event"]);
                Assert.True(problems.Count == 0, action + " as " + actor + ": " + string.Join("; ", problems));
                Assert.Equal("identity." + action, envelope["event"]!["action"]!.GetValue<string>());
                Assert.Equal(e.Hash, envelope["hash"]!.GetValue<string>());
            }
        }

        Assert.True(AllActions.Values.Count > 100);
    }

    [Fact]
    public void TheEventId_IsAUuidV7_AndTheSameEachTimeTheEventIsSent()
    {
        AuditEvent e = Event(7, AuditActions.ConsentGrant);
        string first = SiemFormat.EventId(e);
        Assert.Equal(first, SiemFormat.EventId(Event(7, AuditActions.ConsentGrant)));
        Assert.NotEqual(first, SiemFormat.EventId(Event(8, AuditActions.ConsentGrant)));
        Assert.Matches(UuidV7(), first);
        long ms = Convert.ToInt64(first.Replace("-", string.Empty, StringComparison.Ordinal)[..12], 16);
        Assert.Equal(e.OccurredAt.ToUnixTimeMilliseconds(), ms);
    }

    [PostgresFact]
    public async Task OverTls_EveryNewEventIsSent_ARestartResumes_AndNothingIsLostWhileTheReceiverIsDown()
    {
        using Authority ca = new();
        await using Receiver receiver = new(ca.Server, octetCounted: true);
        await SeedAsync(3);

        await using (SiemForwarder first = Forwarder(receiver.Port, ca.Path, "beginning"))
        {
            Assert.Equal(3, await first.RunOnceAsync());
        }

        await receiver.WaitForAsync(3);
        Assert.All(receiver.Messages, m => Assert.Contains(" sangam - audit - CEF:0|imagiQa|Sangam|", m, StringComparison.Ordinal));
        Assert.Equal(["user.login.success", "user.login.success", "user.login.success"], receiver.Messages.Select(m => Act().Match(m).Groups[1].Value));

        // A restart: only what is new is sent.
        await SeedAsync(2);
        await using (SiemForwarder second = Forwarder(receiver.Port, ca.Path, "beginning"))
        {
            Assert.Equal(2, await second.RunOnceAsync());
        }

        await receiver.WaitForAsync(5);
        await using SangamDbContext db = _pg.CreateContext();
        SiemStatus? status = await SiemForwarder.ReadStatusAsync(db);
        Assert.NotNull(status);
        Assert.Equal((5L, 0L, (string?)null), (status.Sent, status.Lag, status.LastError));

        // The receiver is down: nothing is sent, the error is recorded, and the events wait in the audit log.
        int port = receiver.Port;
        await receiver.DisposeAsync();
        await SeedAsync(1);
        await using (SiemForwarder down = Forwarder(port, ca.Path, "beginning"))
        {
            Assert.Equal(0, await down.RunOnceAsync());
        }

        status = await SiemForwarder.ReadStatusAsync(db);
        Assert.NotNull(status!.LastError);
        Assert.Equal(1, status.Lag);
    }

    [PostgresFact]
    public async Task AReceiverWhoseCertificateTheAuthorityDidNotSign_IsNotSentAnything()
    {
        using Authority trusted = new();
        using Authority other = new();
        await using Receiver receiver = new(other.Server, octetCounted: true);
        await SeedAsync(2);

        await using SiemForwarder forwarder = Forwarder(receiver.Port, trusted.Path, "beginning");
        Assert.Equal(0, await forwarder.RunOnceAsync());

        await Task.Delay(300);
        Assert.Empty(receiver.Messages);
        await using SangamDbContext db = _pg.CreateContext();
        Assert.Contains("Authentication", (await SiemForwarder.ReadStatusAsync(db))!.LastError, StringComparison.Ordinal);
    }

    [PostgresFact]
    public async Task TheJsonFormat_SendsOneLinePerEvent_InTheSharedSchema_AndAFirstStartBeginsNow()
    {
        using Authority ca = new();
        await using Receiver receiver = new(ca.Server, octetCounted: false);
        await SeedAsync(2);

        await using SiemForwarder forwarder = Forwarder(receiver.Port, ca.Path, "now", format: "json");
        Assert.Equal(0, await forwarder.RunOnceAsync());
        await SeedAsync(1);
        Assert.Equal(1, await forwarder.RunOnceAsync());

        await receiver.WaitForAsync(1);
        JsonNode line = JsonNode.Parse(Assert.Single(receiver.Messages))!;
        Assert.Equal(SiemFormat.EnvelopeSchema, line["schema"]!.GetValue<string>());
        Assert.Empty(SangamAudit.Validate(line["event"]));
        Assert.Equal("identity.user.login.success", line["event"]!["action"]!.GetValue<string>());
        Assert.Equal("203.0.113.9", line["event"]!["client"]!["ip"]!.GetValue<string>());
    }

    private static AuditEvent Event(long id, string action, string metadata = "{}", AuditActorType actor = AuditActorType.User, string? ip = "203.0.113.9")
        => new()
        {
            Id = id,
            Action = action,
            ActorType = actor,
            ActorUserId = actor is AuditActorType.System ? null : Guid.Parse("0192a6b0-0000-7000-8000-0000000000aa"),
            ActorAppId = actor is AuditActorType.Api ? Guid.Parse("0192a6b0-0000-7000-8000-0000000000bb") : null,
            TargetType = "organisation",
            TargetId = Guid.Parse("0192a6b0-0000-7000-8000-00000000a001"),
            Metadata = metadata,
            IpAddress = ip,
            UserAgent = ip is null ? null : "Mozilla/5.0 (X11; Linux x86_64)",
            OccurredAt = new DateTimeOffset(2026, 10, 7, 9, 41, 12, 408, TimeSpan.Zero),
            Hash = new string('a', 64),
            PrevHash = new string('b', 64),
        };

    private SiemForwarder Forwarder(int port, string caPath, string startFrom, string format = "cef")
    {
        SiemOptions options = new()
        {
            Enabled = true,
            Host = "localhost",
            Port = port,
            Format = format,
            Tls = true,
            CaCertificatePath = caPath,
            StartFrom = startFrom,
            BatchSize = 2,
        };
        ServiceCollection services = new();
        services.AddScoped(_ => _pg.CreateContext());
        ServiceProvider provider = services.BuildServiceProvider();
        return new SiemForwarder(provider.GetRequiredService<IServiceScopeFactory>(), options, new SystemClock(), NullLogger<SiemForwarder>.Instance, new TestEnvironment());
    }

    private async Task SeedAsync(int count)
    {
        EfAuditWriter writer = new(new ContextFactory(_pg), new SystemClock());
        for (int i = 0; i < count; i++)
        {
            await writer.WriteAsync(new AuditEntry(AuditActions.UserLoginSuccess, AuditActorType.User, Guid.NewGuid(), IpAddress: "203.0.113.9", UserAgent: "Mozilla/5.0"));
        }
    }

    [GeneratedRegex("^[0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$")]
    private static partial Regex UuidV7();

    [GeneratedRegex(" act=([^ ]+) ")]
    private static partial Regex Act();

    /// <summary>Every action name declared in <see cref="AuditActions"/>.</summary>
    private sealed class FieldInfoSet
    {
        public IReadOnlyList<string> Values { get; } = [.. typeof(AuditActions).GetFields()
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)];
    }

    /// <summary>A certificate authority and a "localhost" server certificate it issued.</summary>
    private sealed class Authority : IDisposable
    {
        public Authority()
        {
            using RSA caKey = RSA.Create(2048);
            CertificateRequest caRequest = new("CN=Sangam test SIEM authority", caKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            caRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            caRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
            using X509Certificate2 ca = caRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sangam-siem-ca-" + Guid.NewGuid().ToString("N") + ".crt");
            File.WriteAllText(Path, ca.ExportCertificatePem());

            using RSA serverKey = RSA.Create(2048);
            CertificateRequest request = new("CN=localhost", serverKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            SubjectAlternativeNameBuilder names = new();
            names.AddDnsName("localhost");
            request.CertificateExtensions.Add(names.Build());
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
            using X509Certificate2 issued = request.Create(ca, DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddDays(7), RandomNumberGenerator.GetBytes(8));
            using X509Certificate2 withKey = issued.CopyWithPrivateKey(serverKey);
            Server = X509CertificateLoader.LoadPkcs12(withKey.Export(X509ContentType.Pkcs12), null);
        }

        public string Path { get; }

        public X509Certificate2 Server { get; }

        public void Dispose()
        {
            Server.Dispose();
            File.Delete(Path);
        }
    }

    /// <summary>A TLS syslog receiver on a free port, collecting what arrives.</summary>
    private sealed class Receiver : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;

        public Receiver(X509Certificate2 certificate, bool octetCounted)
        {
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _loop = Task.Run(async () =>
            {
                while (!_stop.IsCancellationRequested)
                {
                    TcpClient client;
                    try
                    {
                        client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    }
                    catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
                    {
                        return;
                    }

                    _ = Task.Run(() => ServeAsync(client, certificate, octetCounted));
                }
            });
        }

        public int Port { get; }

        public ConcurrentQueue<string> Messages { get; } = new();

        public async Task WaitForAsync(int count)
        {
            for (int i = 0; i < 100 && Messages.Count < count; i++)
            {
                await Task.Delay(50);
            }

            Assert.Equal(count, Messages.Count);
        }

        public async ValueTask DisposeAsync()
        {
            if (!_stop.IsCancellationRequested)
            {
                await _stop.CancelAsync();
                _listener.Stop();
                await _loop;
            }

            _stop.Dispose();
        }

        private async Task ServeAsync(TcpClient client, X509Certificate2 certificate, bool octetCounted)
        {
            using (client)
            {
                try
                {
                    await using SslStream ssl = new(client.GetStream());
                    await ssl.AuthenticateAsServerAsync(certificate, clientCertificateRequired: false, checkCertificateRevocation: false);
                    using MemoryStream buffer = new();
                    byte[] chunk = new byte[8192];
                    int read;
                    while ((read = await ssl.ReadAsync(chunk, _stop.Token)) > 0)
                    {
                        buffer.Write(chunk, 0, read);
                        Drain(buffer, octetCounted);
                    }
                }
                catch (Exception ex) when (ex is IOException or OperationCanceledException or System.Security.Authentication.AuthenticationException or ObjectDisposedException)
                {
                    // The forwarder refused the certificate, or the test ended.
                }
            }
        }

        private void Drain(MemoryStream buffer, bool octetCounted)
        {
            byte[] data = buffer.ToArray();
            int at = 0;
            while (true)
            {
                if (octetCounted)
                {
                    int space = Array.IndexOf(data, (byte)' ', at);
                    if (space < 0)
                    {
                        break;
                    }

                    int length = int.Parse(Encoding.ASCII.GetString(data, at, space - at), System.Globalization.CultureInfo.InvariantCulture);
                    if (data.Length < space + 1 + length)
                    {
                        break;
                    }

                    Messages.Enqueue(Encoding.UTF8.GetString(data, space + 1, length));
                    at = space + 1 + length;
                }
                else
                {
                    int newline = Array.IndexOf(data, (byte)'\n', at);
                    if (newline < 0)
                    {
                        break;
                    }

                    Messages.Enqueue(Encoding.UTF8.GetString(data, at, newline - at));
                    at = newline + 1;
                }
            }

            buffer.SetLength(0);
            buffer.Write(data, at, data.Length - at);
        }
    }

    private sealed class ContextFactory : IDbContextFactory<SangamDbContext>
    {
        private readonly PostgresFixture _pg;

        public ContextFactory(PostgresFixture pg)
        {
            _pg = pg;
        }

        public SangamDbContext CreateDbContext() => _pg.CreateContext();
    }

    private sealed class SystemClock : IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    }

    private sealed class TestEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";

        public string ApplicationName { get; set; } = "Sangam.Tests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
