namespace Sangam.Identity.Domain.Entities;

/// <summary>
/// A user who administers one partner app on the admin console: its roles, redirect URIs and
/// the users and organisations that use it. Scoped strictly to <see cref="AppId"/>.
/// </summary>
public sealed class AppAdmin
{
    /// <summary>Primary key.</summary>
    public Guid Id { get; set; }

    /// <summary>The app administered.</summary>
    public Guid AppId { get; set; }

    /// <summary>Navigation to the app.</summary>
    public App? App { get; set; }

    /// <summary>The administrator's user account.</summary>
    public Guid UserId { get; set; }

    /// <summary>Navigation to the user.</summary>
    public SangamUser? User { get; set; }

    /// <summary>Who granted it.</summary>
    public Guid? GrantedByUserId { get; set; }

    /// <summary>When granted (UTC).</summary>
    public DateTimeOffset GrantedAt { get; set; }

    /// <summary>When revoked (UTC); <see langword="null"/> while active.</summary>
    public DateTimeOffset? RevokedAt { get; set; }
}
