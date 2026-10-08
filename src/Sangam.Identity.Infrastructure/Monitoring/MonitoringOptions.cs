using Microsoft.Extensions.Configuration;

namespace Sangam.Identity.Infrastructure.Monitoring;

/// <summary>
/// Monitoring settings under <c>Sangam:Monitoring</c> (D-H). The alert thresholds are under <c>Alerts</c>; every one
/// is a setting, so the founder can tune them without a release.
/// </summary>
public sealed class MonitoringOptions
{
    /// <summary>Configuration section.</summary>
    public const string SectionName = "Sangam:Monitoring";

    /// <summary>Whether this host checks the thresholds and sends alerts. One host does (the identity server, in Compose).</summary>
    public bool Evaluate { get; set; }

    /// <summary>The hosts that should be reporting, comma-separated (for example <c>identity,portal,admin,partner</c>).</summary>
    public string ExpectedHosts { get; set; } = string.Empty;

    /// <summary>The public sites whose TLS certificates are checked, comma-separated host names.</summary>
    public string TlsHosts { get; set; } = string.Empty;

    /// <summary>Where the backup and restore-drill scripts write their status (<c>backup.json</c>, <c>restore-drill.json</c>).</summary>
    public string? StatusDirectory { get; set; }

    /// <summary>Anjal's health endpoint, called every minute (Sangam watches Anjal as Anjal watches Sangam).</summary>
    public string? AnjalHealthUrl { get; set; }

    /// <summary>How long metric rows are kept.</summary>
    public int RetentionDays { get; set; } = 90;

    /// <summary>The thresholds.</summary>
    public AlertThresholds Alerts { get; set; } = new();

    /// <summary>The expected hosts as a list.</summary>
    public IReadOnlyList<string> ExpectedHostList => Split(ExpectedHosts);

    /// <summary>The TLS hosts as a list.</summary>
    public IReadOnlyList<string> TlsHostList => Split(TlsHosts);

    /// <summary>Reads the settings.</summary>
    /// <param name="configuration">Configuration.</param>
    public static MonitoringOptions From(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        MonitoringOptions options = new();
        configuration.GetSection(SectionName).Bind(options);
        return options;
    }

    private static string[] Split(string value)
        => value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

/// <summary>When the founder is alerted (D-H). Per-hour figures are over the last sixty minutes.</summary>
public sealed class AlertThresholds
{
    /// <summary>Refused sign-ins in an hour (a password-guessing wave).</summary>
    public int SignInFailuresPerHour { get; set; } = 200;

    /// <summary>Lockouts in an hour.</summary>
    public int LockoutsPerHour { get; set; } = 20;

    /// <summary>Requests refused by the rate limits in an hour.</summary>
    public int RateLimitedPerHour { get; set; } = 500;

    /// <summary>Server errors as a percentage of requests in an hour (only with at least <see cref="MinimumRequests"/>).</summary>
    public double ErrorRatePercent { get; set; } = 5;

    /// <summary>Requests needed in the hour before the error rate and response time count.</summary>
    public int MinimumRequests { get; set; } = 50;

    /// <summary>Average response time in an hour, in milliseconds.</summary>
    public double AverageResponseMs { get; set; } = 1500;

    /// <summary>E-mails Anjal did not take, in an hour.</summary>
    public int EmailFailuresPerHour { get; set; } = 3;

    /// <summary>SMS Anjal did not take, in an hour.</summary>
    public int SmsFailuresPerHour { get; set; } = 3;

    /// <summary>Free disk space, percent.</summary>
    public double DiskFreePercent { get; set; } = 10;

    /// <summary>Days left on any certificate.</summary>
    public int CertificateDays { get; set; } = 21;

    /// <summary>Minutes an expected host may be silent.</summary>
    public int HostSilentMinutes { get; set; } = 3;

    /// <summary>Database query time, in milliseconds.</summary>
    public double DatabaseLatencyMs { get; set; } = 500;

    /// <summary>Hours since the last successful backup.</summary>
    public int BackupAgeHours { get; set; } = 30;

    /// <summary>Days since the last successful restore drill.</summary>
    public int RestoreDrillAgeDays { get; set; } = 35;

    /// <summary>Minutes Anjal may fail its health check.</summary>
    public int AnjalDownMinutes { get; set; } = 3;

    /// <summary>Days before the breached-password list is due for a refresh (when the check is switched on).</summary>
    public int BreachListAgeDays { get; set; } = 120;

    /// <summary>Hours between repeated alerts for a condition that stays open.</summary>
    public int RepeatHours { get; set; } = 6;
}
