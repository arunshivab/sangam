namespace Sangam.Identity.Domain.Entities;

/// <summary>
/// The heart of the model: user U is staff at organisation O for app A, holding role R there. rc.12 (founder decisions,
/// 14 Oct 2026): one row per role, so a person can hold several roles at one branch (doctor and administrator), and the
/// role may be empty (staff at that branch, roles kept in the application). The unique key is (user, org, app, role)
/// among non-revoked rows. A membership is in that organisation only, never the ones below it. Never deleted — revoked
/// with <see cref="RevokedAt"/>; a re-grant is a new row, preserving history.
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

    /// <summary>The role held, which must belong to <see cref="AppId"/>; <see langword="null"/> for staff whose roles the
    /// application keeps itself (rc.12).</summary>
    public Guid? RoleId { get; set; }

    /// <summary>Navigation to the role.</summary>
    public Role? Role { get; set; }

    /// <summary>Who granted it; <see langword="null"/> for system grants.</summary>
    public Guid? GrantedByUserId { get; set; }

    /// <summary>When granted (UTC).</summary>
    public DateTimeOffset GrantedAt { get; set; }

    /// <summary>When revoked (UTC); <see langword="null"/> while active.</summary>
    public DateTimeOffset? RevokedAt { get; set; }

    /// <summary>
    /// When a time-limited membership ends (PR-25: contractors, auditors); null for one that lasts until revoked. From
    /// then on it no longer counts, and the expiry sweep revokes it.
    /// </summary>
    public DateTimeOffset? ExpiresAt { get; set; }

    /// <summary>Who revoked it.</summary>
    public Guid? RevokedByUserId { get; set; }
}
