namespace Sangam.Identity.Domain.Enums;

/// <summary>Roles a platform operator (imagiQa staff) can hold on the admin console.</summary>
public enum PlatformRole
{
    /// <summary>Read-only access to users, organisations, apps and the audit log.</summary>
    Support = 0,

    /// <summary>Full operational access: suspend users, register apps, manage organisations.</summary>
    Operator = 1,

    /// <summary>Everything an operator can do plus managing other platform operators.</summary>
    Owner = 2,
}
