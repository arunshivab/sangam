namespace Sangam.Identity.Domain.Entities;

/// <summary>
/// A user's recorded agreement that an app may receive the listed scopes, at a specific version
/// of the consent terms. One row per grant; a new consent version or a revocation produces a new
/// row, so the history is a DPDPA-grade record.
/// </summary>
public sealed class Consent
{
    /// <summary>Primary key.</summary>
    public Guid Id { get; set; }

    /// <summary>The consenting user.</summary>
    public Guid UserId { get; set; }

    /// <summary>Navigation to the user.</summary>
    public SangamUser? User { get; set; }

    /// <summary>The app consented to.</summary>
    public Guid AppId { get; set; }

    /// <summary>Navigation to the app.</summary>
    public App? App { get; set; }

    /// <summary>Space-separated scope string exactly as granted ("openid profile email orgs.read").</summary>
    public string Scope { get; set; } = string.Empty;

    /// <summary>Version of the consent wording the user saw ("v1").</summary>
    public string ConsentVersion { get; set; } = string.Empty;

    /// <summary>Client IP at the time of consent.</summary>
    public string? IpAddress { get; set; }

    /// <summary>User agent at the time of consent.</summary>
    public string? UserAgent { get; set; }

    /// <summary>When granted (UTC).</summary>
    public DateTimeOffset GrantedAt { get; set; }

    /// <summary>When revoked (UTC); <see langword="null"/> while active.</summary>
    public DateTimeOffset? RevokedAt { get; set; }
}
