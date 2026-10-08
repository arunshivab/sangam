namespace Sangam.Identity.Domain.Enums;

/// <summary>Where an SMS stands with the provider (SGM-206 §5).</summary>
public enum SmsStatus
{
    /// <summary>Recorded, not yet accepted by a provider.</summary>
    Queued = 0,

    /// <summary>Accepted by the provider.</summary>
    Sent = 1,

    /// <summary>The provider reported delivery to the handset.</summary>
    Delivered = 2,

    /// <summary>Refused by every provider, or reported undelivered.</summary>
    Failed = 3,
}
