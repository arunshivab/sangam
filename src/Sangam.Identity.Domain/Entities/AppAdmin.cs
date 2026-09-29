using Sangam.Identity.Domain.Enums;

namespace Sangam.Identity.Domain.Entities;

/// <summary>
/// A partner's own staff member who administers one application on the partner console: its roles,
/// organisations, memberships, branding and sign-in policy. Scoped strictly to <see cref="AppId"/>.
/// Redirect URIs and client secrets are deliberately not theirs to change — see ADR-0006.
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

    /// <summary>How much they may do over the application.</summary>
    public AppAdminRole Role { get; set; } = AppAdminRole.Admin;

    /// <summary>Who granted it.</summary>
    public Guid? GrantedByUserId { get; set; }

    /// <summary>When granted (UTC).</summary>
    public DateTimeOffset GrantedAt { get; set; }

    /// <summary>When revoked (UTC); <see langword="null"/> while active.</summary>
    public DateTimeOffset? RevokedAt { get; set; }
}
