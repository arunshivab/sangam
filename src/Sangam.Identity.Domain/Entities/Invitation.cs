namespace Sangam.Identity.Domain.Entities;

/// <summary>
/// An invitation by e-mail to an organisation and role in an application (PR-13). Only a hash of
/// the token is kept. The person is linked to the application only when they accept, signed in with
/// the invited address — partners still never see people who have not acted (D-093).
/// </summary>
public class Invitation
{
    /// <summary>Identifier.</summary>
    public Guid Id { get; set; }

    /// <summary>The application.</summary>
    public Guid AppId { get; set; }

    /// <summary>The organisation.</summary>
    public Guid OrgId { get; set; }

    /// <summary>The role offered.</summary>
    public string RoleCode { get; set; } = string.Empty;

    /// <summary>Whether the role applies to organisations below.</summary>
    public bool AppliesToDescendants { get; set; }

    /// <summary>The invited address, as typed.</summary>
    public string Email { get; set; } = string.Empty;

    /// <summary>The invited address, normalised.</summary>
    public string NormalizedEmail { get; set; } = string.Empty;

    /// <summary>SHA-256 of the token (hex). The token itself is only in the e-mail.</summary>
    public string TokenHash { get; set; } = string.Empty;

    /// <summary>The administrator who invited.</summary>
    public Guid InvitedByUserId { get; set; }

    /// <summary>When it was sent.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>When it stops working.</summary>
    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>When it was accepted.</summary>
    public DateTimeOffset? AcceptedAt { get; set; }

    /// <summary>Who accepted it.</summary>
    public Guid? AcceptedByUserId { get; set; }

    /// <summary>When it was withdrawn.</summary>
    public DateTimeOffset? RevokedAt { get; set; }
}
