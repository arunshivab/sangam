using Sangam.Identity.Domain.Enums;

namespace Sangam.Identity.Domain.Entities;

/// <summary>
/// One SMS sent by Sangam (SGM-206 §5). Neither the number nor the code is stored: the number is
/// kept only as a keyed hash, enough to apply per-number limits and to match delivery reports.
/// </summary>
public sealed class SmsMessage
{
    /// <summary>Primary key.</summary>
    public Guid Id { get; set; }

    /// <summary>The account the message was for, when known.</summary>
    public Guid? UserId { get; set; }

    /// <summary>Keyed hash (HMAC-SHA256) of the destination number in E.164 form.</summary>
    public string ToHash { get; set; } = string.Empty;

    /// <summary>Keyed hash of the requesting IP address, for per-IP limits; <see langword="null"/> when unknown.</summary>
    public string? IpHash { get; set; }

    /// <summary>Template key, for example <c>sign_in</c>.</summary>
    public string Template { get; set; } = string.Empty;

    /// <summary>The provider that accepted (or last refused) the message.</summary>
    public string Provider { get; set; } = string.Empty;

    /// <summary>The provider's own id for the message, used to match delivery reports.</summary>
    public string? ProviderMessageId { get; set; }

    /// <summary>Status.</summary>
    public SmsStatus Status { get; set; }

    /// <summary>The provider's refusal or failure reason, without personal data.</summary>
    public string? Error { get; set; }

    /// <summary>When it was recorded (UTC).</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>When delivery was reported (UTC).</summary>
    public DateTimeOffset? DeliveredAt { get; set; }
}
