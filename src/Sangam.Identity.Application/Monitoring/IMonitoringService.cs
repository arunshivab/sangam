using Sangam.Identity.Application.Security;

namespace Sangam.Identity.Application.Monitoring;

/// <summary>Whether a host has reported in recently.</summary>
/// <param name="Host">The host (identity, portal, admin, partner).</param>
/// <param name="LastSeen">When it last wrote its metrics.</param>
/// <param name="Up">Whether that was within the last few minutes.</param>
public sealed record HostStatus(string Host, DateTimeOffset? LastSeen, bool Up);

/// <summary>The database as the monitoring page sees it.</summary>
/// <param name="Up">Whether it answered.</param>
/// <param name="LatencyMs">How long a trivial query took.</param>
/// <param name="SizeBytes">The database's size.</param>
/// <param name="Connections">Open connections to it.</param>
/// <param name="Version">The server's version string.</param>
public sealed record DatabaseStatus(bool Up, double LatencyMs, long SizeBytes, int Connections, string? Version);

/// <summary>One hour of activity, across every host.</summary>
/// <param name="Hour">The start of the hour (UTC).</param>
/// <param name="SignIns">Completed sign-ins.</param>
/// <param name="SignInFailures">Refused sign-ins.</param>
/// <param name="Lockouts">Lockouts reached.</param>
/// <param name="RateLimited">Requests refused by the rate limits (429).</param>
/// <param name="Requests">Requests handled.</param>
/// <param name="Errors">Requests that failed on the server (5xx).</param>
/// <param name="AverageMs">Average response time.</param>
/// <param name="MaxMs">Slowest response.</param>
/// <param name="EmailsFailed">E-mails Anjal did not take.</param>
/// <param name="SmsFailed">SMS Anjal did not take.</param>
public sealed record HourBucket(DateTimeOffset Hour, long SignIns, long SignInFailures, long Lockouts, long RateLimited, long Requests, long Errors, double AverageMs, double MaxMs, long EmailsFailed, long SmsFailed)
{
    /// <summary>Server errors as a share of requests, in percent.</summary>
    public double ErrorPercent => Requests == 0 ? 0 : 100.0 * Errors / Requests;
}

/// <summary>A gauge's latest reading on one host.</summary>
/// <param name="Host">The host.</param>
/// <param name="Subject">What it measures (a disk path, a certificate).</param>
/// <param name="Value">The reading.</param>
/// <param name="At">When.</param>
public sealed record GaugeReading(string Host, string Subject, double Value, DateTimeOffset At);

/// <summary>The certificate a public site presents.</summary>
/// <param name="Host">The site.</param>
/// <param name="NotAfter">When it expires.</param>
/// <param name="Problem">Why it could not be read.</param>
public sealed record TlsStatus(string Host, DateTimeOffset? NotAfter, string? Problem);

/// <summary>The last run of a scheduled job on the server (the backup, the restore drill).</summary>
/// <param name="At">When it last reported.</param>
/// <param name="Ok">Whether it succeeded.</param>
/// <param name="Detail">What it said.</param>
public sealed record JobStatus(DateTimeOffset? At, bool? Ok, string? Detail);

/// <summary>Anjal, Sangam's messaging gateway, as seen from Sangam.</summary>
/// <param name="Configured">Whether Anjal's API is configured here.</param>
/// <param name="LastChecked">When its health endpoint was last called.</param>
/// <param name="Up">Whether it answered then.</param>
/// <param name="EmailsSent">E-mails Anjal accepted in the last 24 hours.</param>
/// <param name="EmailsFailed">E-mails it did not, in the last 24 hours.</param>
/// <param name="SmsSent">SMS accepted in the last 24 hours.</param>
/// <param name="SmsFailed">SMS refused in the last 24 hours.</param>
public sealed record AnjalStatus(bool Configured, DateTimeOffset? LastChecked, bool? Up, long EmailsSent, long EmailsFailed, long SmsSent, long SmsFailed);

/// <summary>An alert condition.</summary>
/// <param name="Key">The condition.</param>
/// <param name="Summary">What was said.</param>
/// <param name="OpenedAt">When it opened.</param>
/// <param name="ResolvedAt">When it cleared.</param>
public sealed record AlertRow(string Key, string Summary, DateTimeOffset OpenedAt, DateTimeOffset? ResolvedAt);

/// <summary>The grievance log at a glance (D-D).</summary>
/// <param name="Open">Open grievances.</param>
/// <param name="AcknowledgeOverdue">Not acknowledged within two working days.</param>
/// <param name="ResolveOverdue">Not resolved within thirty days.</param>
public sealed record GrievanceCounts(int Open, int AcknowledgeOverdue, int ResolveOverdue);

/// <summary>The audit archive's state (D-A), as the archiving host last reported it.</summary>
/// <param name="Enabled">Whether archiving is set up.</param>
/// <param name="LastRun">When it last ran.</param>
/// <param name="Files">Archive files on disk.</param>
/// <param name="Events">Events in them.</param>
/// <param name="OldestArchived">The oldest archived event.</param>
/// <param name="OldestLive">The oldest event still in the live table.</param>
/// <param name="LiveDays">Days an event stays live.</param>
/// <param name="PurgeYears">Years an archive is kept.</param>
public sealed record AuditArchiveStatus(bool Enabled, DateTimeOffset LastRun, int Files, long Events, DateTimeOffset? OldestArchived, DateTimeOffset? OldestLive, int LiveDays, int PurgeYears);

/// <summary>What the SIEM stream has done, kept in <c>host_reports</c> and shown on the monitoring page (PR-32).</summary>
/// <param name="LastId">The last audit id sent: the position a restart resumes from.</param>
/// <param name="Sent">Events sent since streaming began.</param>
/// <param name="Lag">Events recorded but not yet sent, when last checked.</param>
/// <param name="Receiver">host:port.</param>
/// <param name="Format">cef or json.</param>
/// <param name="Tls">Whether the connection used TLS.</param>
/// <param name="CheckedAt">When the stream last looked for events.</param>
/// <param name="LastSentAt">When an event was last sent.</param>
/// <param name="LastError">The last failure, or <see langword="null"/> when the last round succeeded.</param>
/// <param name="LastErrorAt">When the last failure happened.</param>
public sealed record SiemStatus(long LastId, long Sent, long Lag, string Receiver, string Format, bool Tls, DateTimeOffset CheckedAt, DateTimeOffset? LastSentAt, string? LastError, DateTimeOffset? LastErrorAt);

/// <summary>The breached-password list as one host reported it (V-10).</summary>
/// <param name="Host">The host.</param>
/// <param name="Status">Its list's state.</param>
/// <param name="ReportedAt">When it reported.</param>
public sealed record HostBreachList(string Host, BreachListStatus Status, DateTimeOffset ReportedAt);

/// <summary>Everything on the monitoring page (D-H).</summary>
/// <param name="At">When it was taken.</param>
/// <param name="Hosts">The hosts.</param>
/// <param name="Database">The database.</param>
/// <param name="LastHour">The last sixty minutes.</param>
/// <param name="Hours">The last 24 hours, hour by hour, newest first.</param>
/// <param name="Disks">Free disk space.</param>
/// <param name="Certificates">Days left on the token and key-ring certificates.</param>
/// <param name="Tls">The public sites' certificates.</param>
/// <param name="Backup">The last backup.</param>
/// <param name="RestoreDrill">The last restore drill.</param>
/// <param name="BreachList">The breached-password check (rc.5).</param>
/// <param name="Anjal">Anjal.</param>
/// <param name="Alerts">Open alerts, and those resolved in the last week.</param>
public sealed record MonitoringSnapshot(
    DateTimeOffset At,
    IReadOnlyList<HostStatus> Hosts,
    DatabaseStatus Database,
    HourBucket LastHour,
    IReadOnlyList<HourBucket> Hours,
    IReadOnlyList<GaugeReading> Disks,
    IReadOnlyList<GaugeReading> Certificates,
    IReadOnlyList<TlsStatus> Tls,
    JobStatus Backup,
    JobStatus RestoreDrill,
    BreachListStatus BreachList,
    AnjalStatus Anjal,
    IReadOnlyList<AlertRow> Alerts)
{
    /// <summary>
    /// Every host's report of its breached-password list (V-10); <see cref="BreachList"/> is the identity server's,
    /// the one that checks passwords.
    /// </summary>
    public IReadOnlyList<HostBreachList> BreachListReports { get; init; } = [];

    /// <summary>The host whose report <see cref="BreachList"/> is, and when it reported; <see langword="null"/> when no host has reported.</summary>
    public HostBreachList? BreachListSource { get; init; }

    /// <summary>The audit archive (D-A); <see langword="null"/> when it has never run.</summary>
    public AuditArchiveStatus? AuditArchive { get; init; }

    /// <summary>SIEM streaming (PR-32); <see langword="null"/> when it has never run.</summary>
    public SiemStatus? Siem { get; init; }

    /// <summary>The grievance log's open and overdue counts (D-D).</summary>
    public GrievanceCounts Grievances { get; init; } = new(0, 0, 0);

    /// <summary>The virus scanner uploaded logos go through (rc.5).</summary>
    public AntivirusStatus Antivirus { get; init; } = new(false, null);
}

/// <summary>The virus scanner, as this host reaches it (rc.5, ASVS V12.4.2).</summary>
/// <param name="Configured">Whether a scanner is configured.</param>
/// <param name="Up">Whether it answered just now; <see langword="null"/> when none is configured.</param>
public sealed record AntivirusStatus(bool Configured, bool? Up);

/// <summary>Sangam's own monitoring (D-H): no third party; the numbers live in Sangam's database.</summary>
public interface IMonitoringService
{
    /// <summary>The monitoring page's numbers, for a platform operator; <see langword="null"/> for anyone else.</summary>
    /// <param name="operatorUserId">The operator.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<MonitoringSnapshot?> SnapshotAsync(Guid operatorUserId, CancellationToken cancellationToken = default);
}
