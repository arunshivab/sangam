namespace Sangam.Identity.Domain.Entities;

/// <summary>
/// A role in an app's own vocabulary ("doctor" in LiPi HIS). Roles are owned and managed by
/// the app — through the app-admin console or the management API — never by organisations.
/// A role with <see cref="OrgId"/> set is visible only inside that organisation (an
/// org-specific role the app created on the organisation's behalf); <see langword="null"/>
/// means app-wide. Permissions are opaque JSON that only the app interprets.
/// </summary>
public sealed class Role
{
    /// <summary>Primary key.</summary>
    public Guid Id { get; set; }

    /// <summary>Owning app.</summary>
    public Guid AppId { get; set; }

    /// <summary>Navigation to the owning app.</summary>
    public App? App { get; set; }

    /// <summary>Organisation this role is restricted to, or <see langword="null"/> for app-wide.</summary>
    public Guid? OrgId { get; set; }

    /// <summary>Navigation to the restricting organisation.</summary>
    public Organisation? Org { get; set; }

    /// <summary>Stable lowercase code carried in tokens ("doctor"). Unique per app and org scope.</summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>Human-readable name shown in consoles and invites.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Optional description shown to org admins when assigning.</summary>
    public string? Description { get; set; }

    /// <summary>JSON array of permission strings, verbatim into tokens. Opaque to Sangam.</summary>
    public string Permissions { get; set; } = "[]";

    /// <summary>Created automatically at app registration (<c>org_admin</c>); cannot be retired.</summary>
    public bool IsSystem { get; set; }

    /// <summary>When the role was created (UTC).</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>When the role was retired (UTC); existing memberships keep working until reassigned.</summary>
    public DateTimeOffset? RetiredAt { get; set; }
}
