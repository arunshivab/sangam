namespace Sangam.Identity.Domain.Entities;

/// <summary>
/// rc.11 (R5; founder decision, 13 Oct 2026): a client secret an application's owner made on the partner console. Two
/// secrets can be live at once — the one Sangam issued, or an earlier one of the owner's, and a new one — so the
/// application's servers can move to the new secret before the old one is revoked, with no break in sign-in. Each lasts
/// twelve months. Only a hash is kept; the secret is shown once, when it is made.
/// </summary>
public sealed class ClientSecret
{
    /// <summary>How long a secret made on the partner console lasts.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(365);

    /// <summary>How soon before it ends a secret is flagged.</summary>
    public static readonly TimeSpan Warning = TimeSpan.FromDays(30);

    /// <summary>The most secrets live at once, the one Sangam issued included.</summary>
    public const int MaxLive = 2;

    /// <summary>Primary key.</summary>
    public Guid Id { get; set; }

    /// <summary>The application.</summary>
    public Guid AppId { get; set; }

    /// <summary>The secret's hash, in OpenIddict's own format.</summary>
    public string Hash { get; set; } = string.Empty;

    /// <summary>The first and last few characters, so people can tell secrets apart: <c>sgs_9f2a…c41</c>.</summary>
    public string Hint { get; set; } = string.Empty;

    /// <summary>When it was made.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Who made it.</summary>
    public Guid CreatedByUserId { get; set; }

    /// <summary>When it stops working.</summary>
    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>When it was revoked, if it was.</summary>
    public DateTimeOffset? RevokedAt { get; set; }

    /// <summary>Who revoked it.</summary>
    public Guid? RevokedByUserId { get; set; }

    /// <summary>When an application last used it to get a token (to the hour).</summary>
    public DateTimeOffset? LastUsedAt { get; set; }

    /// <summary>rc.15 (ASVS V2.10.1): when its owners were told it ends soon, so they are told once.</summary>
    public DateTimeOffset? ReminderSentAt { get; set; }

    /// <summary>Whether it works at <paramref name="now"/>.</summary>
    /// <param name="now">The time.</param>
    public bool IsLive(DateTimeOffset now) => RevokedAt is null && ExpiresAt > now;
}
