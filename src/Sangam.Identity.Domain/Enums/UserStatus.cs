namespace Sangam.Identity.Domain.Enums;

/// <summary>Lifecycle state of a <see cref="Entities.SangamUser"/>. Stored as lowercase text.</summary>
public enum UserStatus
{
    /// <summary>Normal account.</summary>
    Active = 0,

    /// <summary>Blocked by a platform operator; cannot sign in, data retained.</summary>
    Suspended = 1,

    /// <summary>User requested deletion; 30-day grace period during which the account can be restored.</summary>
    DeletedSoft = 2,

    /// <summary>Personal data removed and email pseudonymised; the row remains for audit integrity only.</summary>
    DeletedHard = 3,
}
