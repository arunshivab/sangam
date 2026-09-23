namespace Sangam.Identity.Domain.Enums;

/// <summary>Lifecycle state of a partner <see cref="Entities.App"/>. Stored as lowercase text.</summary>
public enum AppStatus
{
    /// <summary>Tokens are issued for this app.</summary>
    Active = 0,

    /// <summary>Disabled by a platform operator or on partner exit; no tokens are issued, records retained.</summary>
    Disabled = 1,
}
