namespace Sangam.Identity.Domain.Entities;

/// <summary>What an <see cref="OrgAdmin"/> right allows (rc.12).</summary>
public enum OrgAdminRight
{
    /// <summary>Administers the organisation and everything below it: staff, roles, branch admins, rules, reviews, audit.
    /// At a customer (the top of a tree) this is the customer admin; lower down, a branch admin.</summary>
    Admin = 0,

    /// <summary>Reviews emergency ("break-glass") access in the organisation and below it (M12): typically the Medical
    /// Superintendent or Quality, not IT.</summary>
    EmergencyReviewer = 1,
}

/// <summary>
/// rc.12 (founder decisions, 14 Oct 2026): a right in Sangam over one organisation and its subtree, for one application.
/// Kept apart from roles: an "Administrator" role inside the HIS grants nothing in Sangam, and these rights are not roles
/// the application sees. Never deleted; revoked with <see cref="RevokedAt"/>.
/// </summary>
public sealed class OrgAdmin
{
    /// <summary>Primary key.</summary>
    public Guid Id { get; set; }

    /// <summary>The application whose tree this is.</summary>
    public Guid AppId { get; set; }

    /// <summary>The organisation; the right covers it and everything below it.</summary>
    public Guid OrgId { get; set; }

    /// <summary>Navigation to the organisation.</summary>
    public Organisation? Org { get; set; }

    /// <summary>Who holds the right.</summary>
    public Guid UserId { get; set; }

    /// <summary>Navigation to the holder.</summary>
    public SangamUser? User { get; set; }

    /// <summary>What the right allows.</summary>
    public OrgAdminRight Right { get; set; }

    /// <summary>Who gave it; <see langword="null"/> when converted from an rc.11 <c>org_admin</c> role.</summary>
    public Guid? GrantedByUserId { get; set; }

    /// <summary>When it was given.</summary>
    public DateTimeOffset GrantedAt { get; set; }

    /// <summary>When it ended, if it did.</summary>
    public DateTimeOffset? RevokedAt { get; set; }

    /// <summary>Who ended it.</summary>
    public Guid? RevokedByUserId { get; set; }
}
