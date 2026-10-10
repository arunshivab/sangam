using System.Globalization;
using Sangam.Identity.Application.Monitoring;

namespace Sangam.Identity.Infrastructure.Monitoring;

/// <summary>One condition that should alert the founder now.</summary>
/// <param name="Key">A stable key, so the same condition is not alerted every minute.</param>
/// <param name="Summary">A few words (an SMS carries the first 30 characters).</param>
/// <param name="Details">The e-mail's detail.</param>
public sealed record AlertCondition(string Key, string Summary, string Details);

/// <summary>Turns a monitoring snapshot and the thresholds into the conditions that should alert (D-H).</summary>
public static class AlertRules
{
    /// <summary>The conditions open now.</summary>
    /// <param name="s">The snapshot.</param>
    /// <param name="t">The thresholds.</param>
    /// <param name="expectedHosts">The hosts that should be reporting.</param>
    /// <param name="anjalConfigured">Whether Anjal's health endpoint is watched.</param>
    public static IReadOnlyList<AlertCondition> Evaluate(MonitoringSnapshot s, AlertThresholds t, IReadOnlyList<string> expectedHosts, bool anjalConfigured)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(t);
        ArgumentNullException.ThrowIfNull(expectedHosts);
        List<AlertCondition> open = [];
        CultureInfo c = CultureInfo.InvariantCulture;

        if (!s.Database.Up)
        {
            open.Add(new("database_down", "database not answering", "The PostgreSQL database did not answer the monitoring query."));
        }
        else if (s.Database.LatencyMs > t.DatabaseLatencyMs)
        {
            open.Add(new("database_slow", "database slow", string.Create(c, $"A trivial query took {s.Database.LatencyMs:F0} ms (threshold {t.DatabaseLatencyMs:F0} ms).")));
        }

        foreach (string host in expectedHosts)
        {
            HostStatus? status = s.Hosts.FirstOrDefault(h => h.Host == host);
            if (status is null || status.LastSeen is null || s.At - status.LastSeen.Value > TimeSpan.FromMinutes(t.HostSilentMinutes))
            {
                open.Add(new("host_down:" + host, host + " host silent", string.Create(c, $"The {host} host has not reported for more than {t.HostSilentMinutes} minutes (last: {status?.LastSeen:u}).")));
            }
        }

        HourBucket h1 = s.LastHour;
        Over(open, "signin_failures", "sign-in failures high", h1.SignInFailures, t.SignInFailuresPerHour, "refused sign-ins in the last hour");
        Over(open, "lockouts", "lockouts high", h1.Lockouts, t.LockoutsPerHour, "lockouts in the last hour");
        Over(open, "rate_limited", "rate limits hit often", h1.RateLimited, t.RateLimitedPerHour, "requests refused by the rate limits in the last hour");
        Over(open, "email_failures", "e-mail via Anjal failing", h1.EmailsFailed, t.EmailFailuresPerHour, "e-mails Anjal did not take in the last hour");
        Over(open, "sms_failures", "SMS via Anjal failing", h1.SmsFailed, t.SmsFailuresPerHour, "SMS Anjal did not take in the last hour");
        if (h1.Requests >= t.MinimumRequests && h1.ErrorPercent > t.ErrorRatePercent)
        {
            open.Add(new("error_rate", "server errors high", string.Create(c, $"{h1.ErrorPercent:F1}% of {h1.Requests} requests failed in the last hour (threshold {t.ErrorRatePercent}%).")));
        }

        if (h1.Requests >= t.MinimumRequests && h1.AverageMs > t.AverageResponseMs)
        {
            open.Add(new("response_time", "responses slow", string.Create(c, $"Average response time {h1.AverageMs:F0} ms in the last hour (threshold {t.AverageResponseMs:F0} ms).")));
        }

        foreach (GaugeReading disk in s.Disks.Where(d => d.Value < t.DiskFreePercent))
        {
            open.Add(new("disk:" + disk.Host + ":" + disk.Subject, "disk space low", string.Create(c, $"{disk.Value:F1}% free on {disk.Subject} ({disk.Host}); threshold {t.DiskFreePercent}%.")));
        }

        foreach (GaugeReading cert in s.Certificates.Where(x => x.Value < t.CertificateDays).GroupBy(x => x.Subject).Select(g => g.First()))
        {
            open.Add(new("cert:" + cert.Subject, cert.Value < 0 ? "certificate unreadable" : "certificate expiring", cert.Value < 0
                ? $"The {cert.Subject} certificate could not be read on {cert.Host}."
                : string.Create(c, $"The {cert.Subject} certificate expires in {cert.Value:F0} days (threshold {t.CertificateDays}).")));
        }

        foreach (TlsStatus tls in s.Tls)
        {
            if (tls.NotAfter is DateTimeOffset notAfter && (notAfter - s.At).TotalDays < t.CertificateDays)
            {
                open.Add(new("tls:" + tls.Host, "site certificate expiring", string.Create(c, $"{tls.Host}'s certificate expires on {notAfter:yyyy-MM-dd}.")));
            }
            else if (tls.Problem is not null)
            {
                open.Add(new("tls:" + tls.Host, "site unreachable", $"{tls.Host}: {tls.Problem}"));
            }
        }

        if (s.Backup.Ok == false)
        {
            open.Add(new("backup", "backup failed", "The last backup reported a failure: " + s.Backup.Detail));
        }
        else if (s.Backup.At is DateTimeOffset backupAt && s.At - backupAt > TimeSpan.FromHours(t.BackupAgeHours))
        {
            open.Add(new("backup", "backup overdue", string.Create(c, $"The last successful backup was {(s.At - backupAt).TotalHours:F0} hours ago.")));
        }
        else if (s.Backup.Ok == true && s.Backup.Detail is string detail && detail.Contains("no off-site target", StringComparison.Ordinal))
        {
            // D-E: a backup that never leaves the server does not survive the server.
            open.Add(new("backup_offsite", "backup not copied off-site", "The last backup stayed on the server: no off-site target is configured (D-E)."));
        }

        if (s.RestoreDrill.Ok == false)
        {
            open.Add(new("restore_drill", "restore drill failed", "The last restore drill failed: " + s.RestoreDrill.Detail));
        }
        else if (s.RestoreDrill.At is DateTimeOffset drillAt && s.At - drillAt > TimeSpan.FromDays(t.RestoreDrillAgeDays))
        {
            open.Add(new("restore_drill", "restore drill overdue", string.Create(c, $"The last restore drill was {(s.At - drillAt).TotalDays:F0} days ago.")));
        }

        if (anjalConfigured && s.Anjal.Up == false && s.Anjal.LastChecked is DateTimeOffset checkedAt && s.At - checkedAt <= TimeSpan.FromMinutes(t.AnjalDownMinutes + 2))
        {
            open.Add(new("anjal_down", "Anjal not answering", "Anjal's health endpoint is not answering. Mail and SMS from Sangam may be failing."));
        }

        // rc.5: the virus scanner not answering — logo uploads are refused until it does.
        if (s.Antivirus.Configured && s.Antivirus.Up == false)
        {
            open.Add(new("antivirus_down", "virus scanner not answering", "The virus scanner (the server's ClamAV, shared with Anjal) is not answering. Logo uploads are refused until it does."));
        }

        // rc.5: the online breached-password service unreachable for a while; the built-in list is checked meanwhile.
        if (s.BreachList.Enabled && s.BreachList.Online && s.BreachList.OnlineFailingSince is DateTimeOffset failingSince
            && s.At - failingSince >= TimeSpan.FromMinutes(t.BreachServiceDownMinutes))
        {
            open.Add(new("breach_list", "breached-password service unreachable", string.Create(c, $"The Pwned Passwords service has not answered since {failingSince:yyyy-MM-dd HH:mm} UTC ({s.BreachList.Problem}). Passwords are checked against the built-in list of {s.BreachList.FallbackEntries:N0} common passwords until it answers again. Check the server's outbound connection to api.pwnedpasswords.com.")));
        }

        // D-D: a grievance past either deadline.
        if (s.Grievances.AcknowledgeOverdue > 0 || s.Grievances.ResolveOverdue > 0)
        {
            open.Add(new("grievance_overdue", "grievance overdue", string.Create(c, $"{s.Grievances.AcknowledgeOverdue} grievance(s) not acknowledged within two working days, {s.Grievances.ResolveOverdue} not resolved within thirty days. Open the console's grievance log.")));
        }

        // D-A: the archive runs hourly; behind by more than a few hours, or live events past their year plus a week.
        if (s.AuditArchive is { Enabled: true } archive)
        {
            if (s.At - archive.LastRun > TimeSpan.FromHours(6))
            {
                open.Add(new("audit_archive", "audit archive not running", string.Create(c, $"The audit archive last ran {(s.At - archive.LastRun).TotalHours:F0} hours ago.")));
            }
            else if (archive.OldestLive is DateTimeOffset oldest && s.At - oldest > TimeSpan.FromDays(archive.LiveDays + 7))
            {
                open.Add(new("audit_archive", "audit archive behind", string.Create(c, $"The oldest live audit event is {(s.At - oldest).TotalDays:F0} days old; D-A keeps {archive.LiveDays} days live.")));
            }
        }

        return open;
    }

    private static void Over(List<AlertCondition> open, string key, string summary, long value, int threshold, string what)
    {
        if (value > threshold)
        {
            open.Add(new(key, summary, string.Create(CultureInfo.InvariantCulture, $"{value} {what} (threshold {threshold}).")));
        }
    }
}
