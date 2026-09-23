namespace Sangam.Identity.Domain.Entities;

/// <summary>
/// The heart of the model: user U holds role R in organisation O for app A. The unique key is
/// (user, org, app) among non-revoked rows, so the same person can be admin in HIS and viewer in
/// Aran for the same hospital. Never deleted — revoked with <see cref="RevokedAt"/>; a re-grant
/// is a new row, preserving history.
/// </summary>
public sealed class OrgMembership
{
    /// <summary>Primary key.</summary>
    public Guid Id { get; set; }

    /// <summary>The member.</summary>
    public Guid UserId { get; set; }

    /// <summary>Navigation to the member.</summary>
    public SangamUser? User { get; set; }

    /// <summary>The organisation node the role is held at.</summary>
    public Guid OrgId { get; set; }

    /// <summary>Navigation to the organisation.</summary>
    public Organisation? Org { get; set; }

    /// <summary>The app context; roles never cross apps.</summary>
    public Guid AppId { get; set; }

    /// <summary>Navigation to the app.</summary>
    public App? App { get; set; }

    /// <summary>The role held. Must belong to <see cref="AppId"/>.</summary>
    public Guid RoleId { get; set; }

    /// <summary>Navigation to the role.</summary>
    public Role? Role { get; set; }

    /// <summary>
    /// When <see langword="true"/> the role also applies to every descendant organisation
    /// (a corporate-level admin acting across all daughter hospitals). Default is explicit-node only.
    /// </summary>
    public bool AppliesToDescendants { get; set; }

    /// <summary>Who granted it; <see langword="null"/> for system grants.</summary>
    public Guid? GrantedByUserId { get; set; }

    /// <summary>When granted (UTC).</summary>
    public DateTimeOffset GrantedAt { get; set; }

    /// <summary>When revoked (UTC); <see langword="null"/> while active.</summary>
    public DateTimeOffset? RevokedAt { get; set; }

    /// <summary>Who revoked it.</summary>
    public Guid? RevokedByUserId { get; set; }
}
