namespace Sangam.Identity.Domain.Entities;

/// <summary>
/// A back-channel logout still to be delivered to an application (PR-20, OpenID Connect Back-Channel Logout 1.0).
/// Any host may queue one when a session ends; the identity server, which holds the signing keys, sends it.
/// </summary>
public sealed class LogoutNotification
{
    /// <summary>Primary key; also the logout token's <c>jti</c>.</summary>
    public Guid Id { get; set; }

    /// <summary>The Sangam session that ended (the OIDC <c>sid</c>).</summary>
    public Guid SessionId { get; set; }

    /// <summary>The application to tell.</summary>
    public Guid AppId { get; set; }

    /// <summary>The person (the token's <c>sub</c>).</summary>
    public Guid UserId { get; set; }

    /// <summary>When it was queued (UTC).</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Delivery attempts so far.</summary>
    public int Attempts { get; set; }

    /// <summary>When the next attempt is due (UTC).</summary>
    public DateTimeOffset NextAttemptAt { get; set; }

    /// <summary>When the application acknowledged it (UTC).</summary>
    public DateTimeOffset? SentAt { get; set; }

    /// <summary>Why the last attempt failed, without personal data.</summary>
    public string? LastError { get; set; }
}
