using Sangam.Identity.Domain.Enums;

namespace Sangam.Identity.Domain.Entities;

/// <summary>A user who may sign in to the admin console. Org-less; MFA is mandatory.</summary>
public sealed class PlatformOperator
{
    /// <summary>Primary key.</summary>
    public Guid Id { get; set; }

    /// <summary>The operator's user account.</summary>
    public Guid UserId { get; set; }

    /// <summary>Navigation to the user.</summary>
    public SangamUser? User { get; set; }

    /// <summary>Level of access on the console.</summary>
    public PlatformRole Role { get; set; } = PlatformRole.Support;

    /// <summary>Who granted it; <see langword="null"/> for the bootstrap owner.</summary>
    public Guid? GrantedByUserId { get; set; }

    /// <summary>When granted (UTC).</summary>
    public DateTimeOffset GrantedAt { get; set; }

    /// <summary>When revoked (UTC); <see langword="null"/> while active.</summary>
    public DateTimeOffset? RevokedAt { get; set; }
}
