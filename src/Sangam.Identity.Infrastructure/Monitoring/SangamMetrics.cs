using System.Diagnostics.Metrics;

namespace Sangam.Identity.Infrastructure.Monitoring;

/// <summary>
/// Sangam's own instruments (D-H), built on .NET's metrics. <see cref="MetricsRecorder"/> stores them, a minute at a
/// time, in Sangam's database; nothing leaves the server.
/// </summary>
public static class SangamMetrics
{
    /// <summary>The meter's name.</summary>
    public const string MeterName = "Sangam.Web";

    /// <summary>Instrument: requests handled, tagged by status class (2xx, 3xx, 4xx, 429, 5xx).</summary>
    public const string Requests = "sangam.http.requests";

    /// <summary>Instrument: request duration in milliseconds.</summary>
    public const string RequestDuration = "sangam.http.duration_ms";

    /// <summary>Instrument: completed sign-ins.</summary>
    public const string SignIns = "sangam.signin.success";

    /// <summary>Instrument: refused sign-ins (wrong password, unknown address, locked).</summary>
    public const string SignInFailures = "sangam.signin.failure";

    /// <summary>Instrument: accounts (or unknown addresses) that just reached the lockout.</summary>
    public const string Lockouts = "sangam.signin.lockout";

    /// <summary>Instrument (gauge): 1 while the host is running.</summary>
    public const string HostUp = "sangam.host.up";

    /// <summary>Instrument (gauge): free disk space, percent, tagged by path.</summary>
    public const string DiskFreePercent = "sangam.disk.free_percent";

    /// <summary>Instrument (gauge): days until a certificate expires, tagged by certificate.</summary>
    public const string CertificateDaysLeft = "sangam.cert.days_left";

    /// <summary>Instrument: checks of Anjal's health endpoint, tagged <c>up</c> or <c>down</c>.</summary>
    public const string AnjalChecks = "sangam.anjal.checks";

    private static readonly Meter Meter = new(MeterName);

    /// <summary>Requests handled.</summary>
    public static Counter<long> RequestCount { get; } = Meter.CreateCounter<long>(Requests);

    /// <summary>Request duration.</summary>
    public static Histogram<double> RequestMilliseconds { get; } = Meter.CreateHistogram<double>(RequestDuration, "ms");

    /// <summary>Completed sign-ins.</summary>
    public static Counter<long> SignInCount { get; } = Meter.CreateCounter<long>(SignIns);

    /// <summary>Refused sign-ins.</summary>
    public static Counter<long> SignInFailureCount { get; } = Meter.CreateCounter<long>(SignInFailures);

    /// <summary>Lockouts.</summary>
    public static Counter<long> LockoutCount { get; } = Meter.CreateCounter<long>(Lockouts);

    /// <summary>Anjal health checks.</summary>
    public static Counter<long> AnjalCheckCount { get; } = Meter.CreateCounter<long>(AnjalChecks);

    /// <summary>The meter, for gauges registered by <see cref="HostProbe"/>.</summary>
    internal static Meter Instance => Meter;

    /// <summary>The status class a status code is counted under; 429 apart, because it means the rate limit.</summary>
    /// <param name="status">The HTTP status code.</param>
    public static string StatusClass(int status) => status == 429 ? "429" : status switch
    {
        >= 500 => "5xx",
        >= 400 => "4xx",
        >= 300 => "3xx",
        _ => "2xx",
    };
}
