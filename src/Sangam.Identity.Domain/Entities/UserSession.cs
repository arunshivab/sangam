namespace Sangam.Identity.Domain.Entities;

/// <summary>
/// One browser sign-in. The session cookie carries this row's id, so the portal can list
/// "where you're signed in" and end a single session without disturbing the others.
/// <para>
/// Only what the request actually tells us is stored: the client IP, the user agent, and an
/// optional label the partner app supplied (<c>sangam_device</c>) — Sangam never guesses a
/// location or a device name.
/// </para>
/// </summary>
public sealed class UserSession
{
    /// <summary>Primary key; travels in the session cookie.</summary>
    public Guid Id { get; set; }

    /// <summary>The signed-in user.</summary>
    public Guid UserId { get; set; }

    /// <summary>Navigation to the user.</summary>
    public SangamUser? User { get; set; }

    /// <summary>The app whose sign-in created this session, when one started the flow.</summary>
    public Guid? AppId { get; set; }

    /// <summary>Navigation to that app.</summary>
    public App? App { get; set; }

    /// <summary>
    /// Label supplied by the partner app on the authorization request ("First Floor Radiology").
    /// Opaque, shown escaped, and <see langword="null"/> when the app sent nothing.
    /// </summary>
    public string? DeviceLabel { get; set; }

    /// <summary>Client IP as seen by the server.</summary>
    public string? IpAddress { get; set; }

    /// <summary>Raw user agent, truncated.</summary>
    public string? UserAgent { get; set; }

    /// <summary>The sign-in mode this session was established with.</summary>
    public Enums.SignInMode SignInMode { get; set; }

    /// <summary>When the session started (UTC).</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>When the session was last seen (UTC); updated at most once per validation interval.</summary>
    public DateTimeOffset LastSeenAt { get; set; }

    /// <summary>When the session ended (UTC); <see langword="null"/> while live.</summary>
    public DateTimeOffset? RevokedAt { get; set; }

    /// <summary>Why it ended: <c>user</c>, <c>user_all</c>, <c>password_reset</c>, <c>admin</c>, <c>expired</c>.</summary>
    public string? RevokedReason { get; set; }
}
