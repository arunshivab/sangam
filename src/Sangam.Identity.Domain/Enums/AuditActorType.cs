namespace Sangam.Identity.Domain.Enums;

/// <summary>In what capacity the actor of an <see cref="Entities.AuditEvent"/> acted. Stored as lowercase text.</summary>
public enum AuditActorType
{
    /// <summary>A person acting on their own account (sign-in, password change, consent).</summary>
    User = 0,

    /// <summary>A person acting on someone else's account or organisation (platform operator, org admin).</summary>
    Admin = 1,

    /// <summary>A partner application's backend calling the management API with client credentials.</summary>
    Api = 2,

    /// <summary>Sangam itself, unattended (scheduled purge, lockout expiry, seeding).</summary>
    System = 3,

    /// <summary>An event before the actor is known (failed sign-in for an unknown email, rate-limit trip).</summary>
    Anonymous = 4,
}
