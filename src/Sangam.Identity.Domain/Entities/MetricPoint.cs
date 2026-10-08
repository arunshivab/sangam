namespace Sangam.Identity.Domain.Entities;

/// <summary>
/// One minute of one metric from one host (D-H): Sangam's own monitoring, stored in its own database, no third party.
/// Counters add up <see cref="Count"/> and <see cref="Sum"/>; timings keep the sum and the largest value; gauges
/// keep the latest reading in <see cref="Sum"/> with a count of one.
/// </summary>
public class MetricPoint
{
    /// <summary>The minute (UTC, seconds zero).</summary>
    public DateTimeOffset Minute { get; set; }

    /// <summary>The host that measured it (identity, portal, admin, partner).</summary>
    public string Host { get; set; } = string.Empty;

    /// <summary>The instrument, for example <c>sangam.http.requests</c>.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The one tag that distinguishes series (a status class, a certificate, a path), or empty.</summary>
    public string Tag { get; set; } = string.Empty;

    /// <summary>How many measurements.</summary>
    public long Count { get; set; }

    /// <summary>Their sum.</summary>
    public double Sum { get; set; }

    /// <summary>The largest.</summary>
    public double Max { get; set; }
}

/// <summary>An alert condition that is, or was, open (D-H), so the founder is told once, not every minute.</summary>
public class MonitoringAlert
{
    /// <summary>The condition, for example <c>email_failures</c> or <c>cert:signing</c>.</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>What was said.</summary>
    public string Summary { get; set; } = string.Empty;

    /// <summary>When it opened.</summary>
    public DateTimeOffset OpenedAt { get; set; }

    /// <summary>When the founder was last alerted about it.</summary>
    public DateTimeOffset LastSentAt { get; set; }

    /// <summary>When it cleared, if it has.</summary>
    public DateTimeOffset? ResolvedAt { get; set; }
}
