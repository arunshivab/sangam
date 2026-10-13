namespace Sangam.Identity.Domain.Entities;

/// <summary>
/// rc.11: a login ID someone changed or removed. For <see cref="LoginIds.ReleaseHold"/> nobody else may take it, so the
/// colleagues used to typing it are not sent to a stranger's account; the person who gave it up may take it back.
/// </summary>
public sealed class LoginIdRelease
{
    /// <summary>Primary key.</summary>
    public Guid Id { get; set; }

    /// <summary>The login ID, lower case.</summary>
    public string LoginId { get; set; } = string.Empty;

    /// <summary>Who held it.</summary>
    public Guid UserId { get; set; }

    /// <summary>When it was given up.</summary>
    public DateTimeOffset ReleasedAt { get; set; }
}
