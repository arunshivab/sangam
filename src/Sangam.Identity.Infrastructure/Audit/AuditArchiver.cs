using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Sangam.Identity.Application.Abstractions;
using Sangam.Identity.Application.Monitoring;
using Sangam.Identity.Domain;
using Sangam.Identity.Domain.Entities;
using Sangam.Identity.Domain.Enums;
using Sangam.Identity.Infrastructure.Persistence;

namespace Sangam.Identity.Infrastructure.Audit;

/// <summary>
/// D-A, run by the hourly maintenance sweep on the host that has the archive set up (the identity server): moves
/// audit events older than a year into encrypted, anonymised archive files, then deletes the archive files whose
/// newest event is older than seven years. One host at a time (a PostgreSQL advisory lock). Each step is itself
/// audited — an <c>audit.archive</c> event with the file's SHA-256 and the chain hashes at both ends, so the live
/// chain vouches for the archive; an <c>audit.archive.purge</c> event for each file deleted. The file is written and
/// flushed before the rows are deleted; a crash between the two leaves a duplicate the reader drops by id.
/// </summary>
public sealed partial class AuditArchiver
{
    /// <summary>PostgreSQL advisory-lock key that keeps archiving to one host at a time.</summary>
    public const long LockKey = 0x5A6E_6761_6D41_7243;

    /// <summary>The <c>host_reports</c> subject for the monitoring page.</summary>
    public const string ReportSubject = "audit_archive";

    private readonly IServiceScopeFactory _scopes;
    private readonly AuditArchiveOptions _options;
    private readonly IClock _clock;
    private readonly ILogger<AuditArchiver> _logger;

    /// <summary>Initialises the archiver.</summary>
    /// <param name="scopes">Scope factory.</param>
    /// <param name="options">Archive settings.</param>
    /// <param name="clock">Clock.</param>
    /// <param name="logger">Logger.</param>
    public AuditArchiver(IServiceScopeFactory scopes, AuditArchiveOptions options, IClock clock, ILogger<AuditArchiver> logger)
    {
        _scopes = scopes ?? throw new ArgumentNullException(nameof(scopes));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>One round: archive what is due, purge what has expired, report. Does nothing when not set up.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<AuditArchiveRun> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
        {
            return new AuditArchiveRun(0, 0, 0);
        }

        using IServiceScope scope = _scopes.CreateScope();
        SangamDbContext db = scope.ServiceProvider.GetRequiredService<SangamDbContext>();
        IAuditWriter audit = scope.ServiceProvider.GetRequiredService<IAuditWriter>();

        // One connection for the whole round, holding a session advisory lock: one host archives at a time.
        await db.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            bool mine = await db.Database.SqlQueryRaw<bool>("SELECT pg_try_advisory_lock(@key) AS \"Value\"", new NpgsqlParameter("key", LockKey))
                .SingleAsync(cancellationToken).ConfigureAwait(false);
            if (!mine)
            {
                return new AuditArchiveRun(0, 0, 0);
            }

            try
            {
                return await ArchiveAndPurgeAsync(db, audit, cancellationToken).ConfigureAwait(false);
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

    private async Task<AuditArchiveRun> ArchiveAndPurgeAsync(SangamDbContext db, IAuditWriter audit, CancellationToken cancellationToken)
    {
        System.IO.Directory.CreateDirectory(_options.Directory!);
        using X509Certificate2 certificate = _options.LoadCertificate();
        int files = 0;
        int archived = 0;
        DateTimeOffset cutoff = _clock.UtcNow - _options.Live;
        while (true)
        {
            List<AuditEvent> due = await db.AuditEvents.AsNoTracking()
                .Where(e => e.OccurredAt < cutoff)
                .OrderBy(e => e.Id)
                .Take(Math.Max(1, _options.BatchSize))
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            if (due.Count == 0)
            {
                break;
            }

            string name = string.Create(CultureInfo.InvariantCulture, $"audit-{due[0].OccurredAt.UtcDateTime:yyyyMMdd}-{due[0].Id:D12}-{due[^1].Id:D12}{AuditArchiveFile.Extension}");
            string path = Path.Combine(_options.Directory!, name);
            if (File.Exists(path))
            {
                // A crash after writing and before deleting: the file holds these very events.
                File.Delete(path);
            }

            // Written and flushed to disk first; only then are the rows deleted.
            AuditArchiveHeader header = AuditArchiveFile.Write(path, due, certificate, _clock.UtcNow);
            string sha = await HashAsync(path, cancellationToken).ConfigureAwait(false);
            long first = due[0].Id;
            long last = due[^1].Id;
            IDbContextTransaction tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using (tx.ConfigureAwait(false))
            {
                await db.Database.ExecuteSqlRawAsync("SELECT set_config('sangam.audit_maintenance', 'on', true);", cancellationToken).ConfigureAwait(false);
                await db.AuditEvents.Where(e => e.Id >= first && e.Id <= last && e.OccurredAt < cutoff).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
                await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
            }

            await audit.WriteAsync(
                new AuditEntry(AuditActions.AuditArchive, AuditActorType.System, Metadata: JsonSerializer.Serialize(new Dictionary<string, object?>
                {
                    ["file"] = name,
                    ["sha256"] = sha,
                    ["count"] = header.Count,
                    ["first_id"] = header.FirstId,
                    ["last_id"] = header.LastId,
                    ["oldest"] = header.Oldest.ToString("O", CultureInfo.InvariantCulture),
                    ["newest"] = header.Newest.ToString("O", CultureInfo.InvariantCulture),
                    ["first_prev_hash"] = header.FirstPrevHash,
                    ["last_hash"] = header.LastHash,
                    ["certificate"] = header.CertificateThumbprint,
                })),
                cancellationToken).ConfigureAwait(false);
            LogArchived(due.Count, name);
            files++;
            archived += due.Count;
        }

        int purged = await PurgeAsync(audit, cancellationToken).ConfigureAwait(false);
        await ReportAsync(db, cancellationToken).ConfigureAwait(false);
        return new AuditArchiveRun(files, archived, purged);
    }

    /// <summary>The archive files, newest last, with their clear-text headers.</summary>
    public IReadOnlyList<(string File, AuditArchiveHeader Header)> List()
    {
        if (!_options.Enabled || !System.IO.Directory.Exists(_options.Directory))
        {
            return [];
        }

        List<(string, AuditArchiveHeader)> files = [];
        foreach (string path in System.IO.Directory.EnumerateFiles(_options.Directory!, "*" + AuditArchiveFile.Extension).Order(StringComparer.Ordinal))
        {
            if (AuditArchiveFile.ReadHeader(path) is AuditArchiveHeader header)
            {
                files.Add((Path.GetFileName(path), header));
            }
        }

        return files;
    }

    private async Task<int> PurgeAsync(IAuditWriter audit, CancellationToken cancellationToken)
    {
        DateTimeOffset expiry = _clock.UtcNow.AddYears(-_options.Years);
        int purged = 0;
        foreach ((string file, AuditArchiveHeader header) in List())
        {
            if (header.Newest >= expiry)
            {
                continue;
            }

            File.Delete(Path.Combine(_options.Directory!, file));
            purged++;
            await audit.WriteAsync(
                new AuditEntry(AuditActions.AuditArchivePurge, AuditActorType.System, Metadata: JsonSerializer.Serialize(new Dictionary<string, object?>
                {
                    ["file"] = file,
                    ["count"] = header.Count,
                    ["newest"] = header.Newest.ToString("O", CultureInfo.InvariantCulture),
                    ["years"] = _options.Years,
                })),
                cancellationToken).ConfigureAwait(false);
            LogPurged(file);
        }

        return purged;
    }

    private async Task ReportAsync(SangamDbContext db, CancellationToken cancellationToken)
    {
        IReadOnlyList<(string File, AuditArchiveHeader Header)> files = List();
        DateTimeOffset? oldestLive = await db.AuditEvents.AsNoTracking().OrderBy(e => e.Id).Select(e => (DateTimeOffset?)e.OccurredAt).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        AuditArchiveStatus status = new(
            true,
            _clock.UtcNow,
            files.Count,
            files.Sum(f => (long)f.Header.Count),
            files.Count == 0 ? null : files.Min(f => f.Header.Oldest),
            oldestLive,
            (int)_options.Live.TotalDays,
            _options.Years);
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO host_reports (host, subject, payload, reported_at) VALUES (@host, @subject, @payload, @at) " +
            "ON CONFLICT (host, subject) DO UPDATE SET payload = excluded.payload, reported_at = excluded.reported_at",
            [
                new NpgsqlParameter("host", "audit"),
                new NpgsqlParameter("subject", ReportSubject),
                new NpgsqlParameter("payload", JsonSerializer.Serialize(status)),
                new NpgsqlParameter("at", _clock.UtcNow),
            ],
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string> HashAsync(string path, CancellationToken cancellationToken)
    {
        FileStream file = File.OpenRead(path);
        await using (file.ConfigureAwait(false))
        {
            return Convert.ToHexStringLower(await SHA256.HashDataAsync(file, cancellationToken).ConfigureAwait(false));
        }
    }

    [LoggerMessage(EventId = 1210, Level = LogLevel.Information, Message = "Audit archive: {Count} event(s) moved to {File}")]
    private partial void LogArchived(int count, string file);

    [LoggerMessage(EventId = 1211, Level = LogLevel.Information, Message = "Audit archive: {File} deleted after seven years")]
    private partial void LogPurged(string file);
}

/// <summary>What one archiving round did.</summary>
/// <param name="Files">Archive files written.</param>
/// <param name="Events">Events moved.</param>
/// <param name="Purged">Archive files deleted at seven years.</param>
public sealed record AuditArchiveRun(int Files, int Events, int Purged);
