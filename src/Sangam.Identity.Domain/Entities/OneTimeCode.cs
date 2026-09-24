using Sangam.Identity.Domain.Enums;

namespace Sangam.Identity.Domain.Entities;

/// <summary>
/// A six-digit code emailed to a user for one purpose. Only the hash is stored; the code is
/// valid for a short window, may be tried a limited number of times and is consumed on success.
/// </summary>
public sealed class OneTimeCode
{
    /// <summary>Primary key.</summary>
    public Guid Id { get; set; }

    /// <summary>The user the code was issued to.</summary>
    public Guid UserId { get; set; }

    /// <summary>Navigation to the user.</summary>
    public SangamUser? User { get; set; }

    /// <summary>What the code proves.</summary>
    public OneTimeCodePurpose Purpose { get; set; }

    /// <summary>SHA-256 of user id + purpose + code, lowercase hex.</summary>
    public string CodeHash { get; set; } = string.Empty;

    /// <summary>When the code was issued (UTC).</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>After this instant the code is refused (UTC).</summary>
    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>Wrong guesses so far.</summary>
    public int FailedAttempts { get; set; }

    /// <summary>When the code was used successfully (UTC); <see langword="null"/> while live.</summary>
    public DateTimeOffset? ConsumedAt { get; set; }
}
