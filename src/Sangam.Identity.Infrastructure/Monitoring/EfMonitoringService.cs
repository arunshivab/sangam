using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Sangam.Identity.Application.Monitoring;
using Sangam.Identity.Application.Security;
using Sangam.Identity.Infrastructure.Messaging;
using Sangam.Identity.Infrastructure.Persistence;

namespace Sangam.Identity.Infrastructure.Monitoring;

/// <summary>
/// The monitoring page's numbers (D-H), read from <c>metric_points</c> and the database itself; the TLS certificates
/// of the public sites, the backup and restore-drill status files, and the breached-password list's state.
/// </summary>
public sealed class EfMonitoringService : IMonitoringService
{
    private static readonly string[] HourNames =
    [
        SangamMetrics.SignIns, SangamMetrics.SignInFailures, SangamMetrics.Lockouts, SangamMetrics.Requests,
        SangamMetrics.RequestDuration, MessagingMetrics.EmailFailed, MessagingMetrics.SmsFailed,
    ];

    private readonly IDbContextFactory<SangamDbContext> _contexts;
    private readonly MonitoringOptions _options;
    private readonly TlsProbe _tls;
    private readonly IBreachListStatus _breaches;
    private readonly AnjalOptions _anjal;

    /// <summary>Initialises the service.</summary>
    /// <param name="contexts">Database contexts.</param>
    /// <param name="options">Monitoring settings.</param>
    /// <param name="tls">TLS certificate probe.</param>
    /// <param name="breaches">The breached-password list's state.</param>
    /// <param name="anjal">Anjal settings.</param>
    public EfMonitoringService(IDbContextFactory<SangamDbContext> contexts, MonitoringOptions options, TlsProbe tls, IBreachListStatus breaches, AnjalOptions anjal)
    {
        _contexts = contexts ?? throw new ArgumentNullException(nameof(contexts));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _tls = tls ?? throw new ArgumentNullException(nameof(tls));
        _breaches = breaches ?? throw new ArgumentNullException(nameof(breaches));
        _anjal = anjal ?? throw new ArgumentNullException(nameof(anjal));
    }

    /// <inheritdoc />
    public async Task<MonitoringSnapshot?> SnapshotAsync(Guid operatorUserId, CancellationToken cancellationToken = default)
    {
        SangamDbContext db = await _contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            if (!await db.PlatformOperators.AnyAsync(o => o.UserId == operatorUserId && o.RevokedAt == null, cancellationToken).ConfigureAwait(false))
            {
                return null;
            }
        }

        return await BuildAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Takes the snapshot (for the page and for the alert evaluator).</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<MonitoringSnapshot> BuildAsync(CancellationToken cancellationToken = default)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        SangamDbContext db = await _contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            DatabaseStatus database = await DatabaseAsync(db, cancellationToken).ConfigureAwait(false);
            if (!database.Up)
            {
                return new MonitoringSnapshot(now, [], database, Empty(now), [], [], [], await _tls.CheckAsync(_options.TlsHostList, cancellationToken).ConfigureAwait(false),
                    ReadJob("backup.json"), ReadJob("restore-drill.json"), _breaches.Status, new AnjalStatus(_anjal.Configured, null, null, 0, 0, 0, 0), []);
            }

            DateTimeOffset since = now.AddHours(-24);
            List<SeriesRow> rows = await db.Database.SqlQueryRaw<SeriesRow>(
                "SELECT date_trunc('hour', minute) AS \"Hour\", name AS \"Name\", tag AS \"Tag\", SUM(count)::bigint AS \"Count\", SUM(sum) AS \"Sum\", MAX(max) AS \"Max\" " +
                "FROM metric_points WHERE minute >= @since AND name = ANY(@names) GROUP BY 1, 2, 3",
                new NpgsqlParameter("since", since), new NpgsqlParameter("names", HourNames.Concat([MessagingMetrics.EmailSent, MessagingMetrics.SmsSent]).ToArray()))
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            List<SeriesRow> lastHourRows = await db.Database.SqlQueryRaw<SeriesRow>(
                "SELECT @hour AS \"Hour\", name AS \"Name\", tag AS \"Tag\", SUM(count)::bigint AS \"Count\", SUM(sum) AS \"Sum\", MAX(max) AS \"Max\" " +
                "FROM metric_points WHERE minute >= @since AND name = ANY(@names) GROUP BY 2, 3",
                new NpgsqlParameter("hour", now.AddHours(-1)), new NpgsqlParameter("since", now.AddHours(-1)), new NpgsqlParameter("names", HourNames))
                .ToListAsync(cancellationToken).ConfigureAwait(false);

            List<HourBucket> hours = [.. rows.GroupBy(r => r.Hour).OrderByDescending(g => g.Key).Select(g => Bucket(g.Key, g))];
            List<LatestRow> latest = await db.Database.SqlQueryRaw<LatestRow>(
                "SELECT DISTINCT ON (host, name, tag) host AS \"Host\", name AS \"Name\", tag AS \"Tag\", sum AS \"Value\", minute AS \"Minute\" " +
                "FROM metric_points WHERE minute >= @since AND name = ANY(@names) ORDER BY host, name, tag, minute DESC",
                new NpgsqlParameter("since", now.AddDays(-7)), new NpgsqlParameter("names", new[] { SangamMetrics.HostUp, SangamMetrics.DiskFreePercent, SangamMetrics.CertificateDaysLeft, SangamMetrics.AnjalChecks }))
                .ToListAsync(cancellationToken).ConfigureAwait(false);

            HashSet<string> hostNames = [.. latest.Where(l => l.Name == SangamMetrics.HostUp).Select(l => l.Host), .. _options.ExpectedHostList];
            List<HostStatus> hosts = [.. hostNames.Order(StringComparer.Ordinal).Select(h =>
            {
                DateTimeOffset? seen = latest.Where(l => l.Name == SangamMetrics.HostUp && l.Host == h).Select(l => (DateTimeOffset?)l.Minute).FirstOrDefault();
                return new HostStatus(h, seen, seen is DateTimeOffset s && now - s <= TimeSpan.FromMinutes(Math.Max(2, _options.Alerts.HostSilentMinutes)));
            })];

            LatestRow? anjal = latest.Where(l => l.Name == SangamMetrics.AnjalChecks).OrderByDescending(l => l.Minute).FirstOrDefault();
            AnjalStatus anjalStatus = new(
                _anjal.Configured,
                anjal?.Minute,
                anjal is null ? null : anjal.Tag == "up",
                Total(rows, MessagingMetrics.EmailSent),
                Total(rows, MessagingMetrics.EmailFailed),
                Total(rows, MessagingMetrics.SmsSent),
                Total(rows, MessagingMetrics.SmsFailed));

            List<AlertRow> alerts = await db.MonitoringAlerts.AsNoTracking()
                .Where(a => a.ResolvedAt == null || a.ResolvedAt > now.AddDays(-7))
                .OrderByDescending(a => a.OpenedAt)
                .Select(a => new AlertRow(a.Key, a.Summary, a.OpenedAt, a.ResolvedAt))
                .ToListAsync(cancellationToken).ConfigureAwait(false);

            // V-10: the list as the identity server sees it — the host that checks passwords — not this host's settings.
            List<HostBreachList> reports = await BreachReportsAsync(db, cancellationToken).ConfigureAwait(false);
            HostBreachList? source = reports.FirstOrDefault(r => r.Host.StartsWith(_options.PasswordHost, StringComparison.OrdinalIgnoreCase));

            return new MonitoringSnapshot(
                now,
                hosts,
                database,
                Bucket(now.AddHours(-1), lastHourRows),
                hours,
                [.. latest.Where(l => l.Name == SangamMetrics.DiskFreePercent).Select(l => new GaugeReading(l.Host, l.Tag, l.Value, l.Minute))],
                [.. latest.Where(l => l.Name == SangamMetrics.CertificateDaysLeft).Select(l => new GaugeReading(l.Host, l.Tag, l.Value, l.Minute))],
                await _tls.CheckAsync(_options.TlsHostList, cancellationToken).ConfigureAwait(false),
                ReadJob("backup.json"),
                ReadJob("restore-drill.json"),
                source?.Status ?? _breaches.Status,
                anjalStatus,
                alerts)
            {
                BreachListReports = reports,
                BreachListSource = source,
                AuditArchive = await AuditArchiveAsync(db, cancellationToken).ConfigureAwait(false),
                Siem = await SiemAsync(db, cancellationToken).ConfigureAwait(false),
                Grievances = new GrievanceCounts(
                    await db.Grievances.CountAsync(g => g.ClosedAt == null, cancellationToken).ConfigureAwait(false),
                    await db.Grievances.CountAsync(g => g.ClosedAt == null && g.AcknowledgedAt == null && g.AcknowledgeBy < now, cancellationToken).ConfigureAwait(false),
                    await db.Grievances.CountAsync(g => g.ClosedAt == null && g.ResolveBy < now, cancellationToken).ConfigureAwait(false)),
            };
        }
    }

    private static async Task<AuditArchiveStatus?> AuditArchiveAsync(SangamDbContext db, CancellationToken cancellationToken)
    {
        string? payload = await db.HostReports.AsNoTracking()
            .Where(r => r.Subject == Audit.AuditArchiver.ReportSubject)
            .OrderByDescending(r => r.ReportedAt)
            .Select(r => r.Payload)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return payload is null ? null : JsonSerializer.Deserialize<AuditArchiveStatus>(payload);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task<SiemStatus?> SiemAsync(SangamDbContext db, CancellationToken cancellationToken)
    {
        try
        {
            return await Siem.SiemForwarder.ReadStatusAsync(db, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task<List<HostBreachList>> BreachReportsAsync(SangamDbContext db, CancellationToken cancellationToken)
    {
        List<HostBreachList> reports = [];
        foreach (Domain.Entities.HostReport row in await db.HostReports.AsNoTracking()
            .Where(r => r.Subject == MetricsRecorder.BreachListSubject)
            .OrderBy(r => r.Host)
            .ToListAsync(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                if (JsonSerializer.Deserialize<BreachListStatus>(row.Payload) is BreachListStatus status)
                {
                    reports.Add(new HostBreachList(row.Host, status, row.ReportedAt));
                }
            }
            catch (JsonException)
            {
                // A report this version cannot read is left out; the host writes a fresh one within a minute.
            }
        }

        return reports;
    }

    private static async Task<DatabaseStatus> DatabaseAsync(SangamDbContext db, CancellationToken cancellationToken)
    {
        try
        {
            long started = Stopwatch.GetTimestamp();
            string version = await db.Database.SqlQueryRaw<string>("SELECT version() AS \"Value\"").SingleAsync(cancellationToken).ConfigureAwait(false);
            double latency = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            long size = await db.Database.SqlQueryRaw<long>("SELECT pg_database_size(current_database()) AS \"Value\"").SingleAsync(cancellationToken).ConfigureAwait(false);
            int connections = await db.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM pg_stat_activity WHERE datname = current_database()").SingleAsync(cancellationToken).ConfigureAwait(false);
            return new DatabaseStatus(true, latency, size, connections, version.Split(" on ", StringSplitOptions.None)[0]);
        }
        catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException or TimeoutException)
        {
            return new DatabaseStatus(false, 0, 0, 0, null);
        }
    }

    private JobStatus ReadJob(string file)
    {
        if (string.IsNullOrWhiteSpace(_options.StatusDirectory))
        {
            return new JobStatus(null, null, null);
        }

        string path = Path.Combine(_options.StatusDirectory, file);
        try
        {
            if (!File.Exists(path))
            {
                return new JobStatus(null, null, null);
            }

            using JsonDocument json = JsonDocument.Parse(File.ReadAllText(path));
            JsonElement root = json.RootElement;
            DateTimeOffset? at = root.TryGetProperty("at", out JsonElement a) && DateTimeOffset.TryParse(a.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset parsed) ? parsed : null;
            bool? ok = root.TryGetProperty("ok", out JsonElement o) && o.ValueKind is JsonValueKind.True or JsonValueKind.False ? o.GetBoolean() : null;
            string? detail = root.TryGetProperty("detail", out JsonElement d) ? d.GetString() : null;
            return new JobStatus(at, ok, detail);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or InvalidOperationException)
        {
            return new JobStatus(null, false, "The status file could not be read.");
        }
    }

    private static HourBucket Bucket(DateTimeOffset hour, IEnumerable<SeriesRow> rows)
    {
        List<SeriesRow> list = [.. rows];
        SeriesRow? duration = list.FirstOrDefault(r => r.Name == SangamMetrics.RequestDuration);
        return new HourBucket(
            hour,
            Total(list, SangamMetrics.SignIns),
            Total(list, SangamMetrics.SignInFailures),
            Total(list, SangamMetrics.Lockouts),
            Total(list, SangamMetrics.Requests, "429"),
            Total(list, SangamMetrics.Requests),
            Total(list, SangamMetrics.Requests, "5xx"),
            duration is null || duration.Count == 0 ? 0 : duration.Sum / duration.Count,
            duration?.Max ?? 0,
            Total(list, MessagingMetrics.EmailFailed),
            Total(list, MessagingMetrics.SmsFailed));
    }

    private static long Total(IEnumerable<SeriesRow> rows, string name, string? tag = null)
        => (long)rows.Where(r => r.Name == name && (tag is null || r.Tag == tag)).Sum(r => r.Sum);

    private static HourBucket Empty(DateTimeOffset now) => new(now.AddHours(-1), 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

    /// <summary>One series over one hour.</summary>
    internal sealed class SeriesRow
    {
        public DateTimeOffset Hour { get; set; }

        public string Name { get; set; } = string.Empty;

        public string Tag { get; set; } = string.Empty;

        public long Count { get; set; }

        public double Sum { get; set; }

        public double Max { get; set; }
    }

    /// <summary>The latest reading of one series.</summary>
    internal sealed class LatestRow
    {
        public string Host { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        public string Tag { get; set; } = string.Empty;

        public double Value { get; set; }

        public DateTimeOffset Minute { get; set; }
    }
}
