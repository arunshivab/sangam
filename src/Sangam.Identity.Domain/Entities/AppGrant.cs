namespace Sangam.Identity.Domain.Entities;

/// <summary>
/// "This user may use this app" — independent of any organisation, so individual users are
/// first-class. Required for any <see cref="OrgMembership"/> in the same app to be effective.
/// Revoked, not deleted.
/// </summary>
public sealed class AppGrant
{
    /// <summary>Primary key.</summary>
    public Guid Id { get; set; }

    /// <summary>The user.</summary>
    public Guid UserId { get; set; }

    /// <summary>Navigation to the user.</summary>
    public SangamUser? User { get; set; }

    /// <summary>The app.</summary>
    public Guid AppId { get; set; }

    /// <summary>Navigation to the app.</summary>
    public App? App { get; set; }

    /// <summary>When granted (UTC).</summary>
    public DateTimeOffset GrantedAt { get; set; }

    /// <summary>When revoked (UTC); <see langword="null"/> while active.</summary>
    public DateTimeOffset? RevokedAt { get; set; }
}
