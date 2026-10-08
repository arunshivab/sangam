using System.Diagnostics.Metrics;

namespace Sangam.Identity.Infrastructure.Messaging;

/// <summary>
/// Counters for messages handed to Anjal (D-H: "Anjal e-mail and SMS delivery failures" on the monitoring page).
/// Built-in .NET metrics; the monitoring collector stores them in Sangam's own database.
/// </summary>
public static class MessagingMetrics
{
    /// <summary>The meter's name.</summary>
    public const string MeterName = "Sangam.Messaging";

    /// <summary>Instrument: e-mails Anjal accepted.</summary>
    public const string EmailSent = "sangam.email.sent";

    /// <summary>Instrument: e-mails that could not be handed to Anjal after every attempt.</summary>
    public const string EmailFailed = "sangam.email.failed";

    /// <summary>Instrument: SMS Anjal accepted.</summary>
    public const string SmsSent = "sangam.sms.sent";

    /// <summary>Instrument: SMS refused or not delivered.</summary>
    public const string SmsFailed = "sangam.sms.failed";

    private static readonly Meter Meter = new(MeterName);

    /// <summary>E-mails accepted.</summary>
    public static Counter<long> EmailsSent { get; } = Meter.CreateCounter<long>(EmailSent);

    /// <summary>E-mails failed.</summary>
    public static Counter<long> EmailsFailed { get; } = Meter.CreateCounter<long>(EmailFailed);

    /// <summary>SMS accepted.</summary>
    public static Counter<long> SmsAccepted { get; } = Meter.CreateCounter<long>(SmsSent);

    /// <summary>SMS failed.</summary>
    public static Counter<long> SmsRefused { get; } = Meter.CreateCounter<long>(SmsFailed);
}
