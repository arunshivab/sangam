namespace Sangam.Identity.Domain.Enums;

/// <summary>Lifecycle state of an <see cref="Entities.Organisation"/>. Stored as lowercase text.</summary>
public enum OrganisationStatus
{
    /// <summary>Normal organisation.</summary>
    Active = 0,

    /// <summary>Blocked by a platform operator; memberships are dormant.</summary>
    Suspended = 1,

    /// <summary>Marked for deletion; can be restored within the grace period.</summary>
    DeletedSoft = 2,

    /// <summary>Removed; the row remains for audit integrity only.</summary>
    DeletedHard = 3,

    /// <summary>rc.12: a customer (a hospital at the top of its tree) that left the application. Every role and admin right
    /// under it ended when it closed; the people keep their own Sangam accounts.</summary>
    Closed = 4,
}
