namespace Sangam.Identity.Domain.Enums;

/// <summary>
/// The rank a partner's own staff member holds over one application, on the partner console.
/// Ordered and cumulative, like <see cref="PlatformRole"/>: a check is <c>role &gt;= AppAdminRole.Owner</c>.
/// </summary>
public enum AppAdminRole
{
    /// <summary>Configures the application's roles, organisations, memberships, branding and sign-in policy.</summary>
    Admin = 0,

    /// <summary>Everything an admin may do, plus adding and removing the application's administrators.</summary>
    Owner = 1,
}
