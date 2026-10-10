using Sangam.Identity.Application.Tenancy;

namespace Sangam.Identity.Application.Partners;

/// <summary>State of an invitation as seen by the invited person.</summary>
public enum InvitationState
{
    /// <summary>Can be accepted.</summary>
    Open,

    /// <summary>Already accepted.</summary>
    Used,

    /// <summary>Past its expiry.</summary>
    Expired,

    /// <summary>Withdrawn, or its inviter is no longer an administrator.</summary>
    Withdrawn,
}

/// <summary>What the invited person sees.</summary>
/// <param name="AppName">The application.</param>
/// <param name="OrgName">The organisation.</param>
/// <param name="RoleName">The role offered.</param>
/// <param name="Email">The invited address.</param>
/// <param name="State">Whether it can be accepted.</param>
public sealed record InvitationView(string AppName, string OrgName, string RoleName, string Email, InvitationState State);

/// <summary>An invitation the management API sent (rc.5).</summary>
/// <param name="Id">The invitation's id.</param>
/// <param name="Email">The invited address.</param>
/// <param name="Role">The role offered.</param>
/// <param name="ExpiresAt">When the link stops working.</param>
public sealed record InvitationSent(Guid Id, string Email, string Role, DateTimeOffset ExpiresAt);

/// <summary>
/// Invitations by e-mail (PR-13): an application administrator invites an address to an
/// organisation and role; the person accepts while signed in with that address.
/// </summary>
public interface IInvitationService
{
    /// <summary>Sends an invitation.</summary>
    /// <param name="inviterUserId">The administrator.</param>
    /// <param name="appId">The application.</param>
    /// <param name="orgId">The organisation.</param>
    /// <param name="email">The address to invite.</param>
    /// <param name="roleCode">The role offered.</param>
    /// <param name="appliesToDescendants">Whether the role applies below the organisation.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    Task<PartnerResult> CreateAsync(Guid inviterUserId, Guid appId, Guid orgId, string email, string roleCode, bool appliesToDescendants, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends an invitation on the application's own behalf, through the management API (rc.5, ASVS V4.2.1): the way
    /// an application brings in someone who does not use it yet. The person is linked only when they accept.
    /// </summary>
    /// <param name="appId">The calling application.</param>
    /// <param name="orgId">The organisation.</param>
    /// <param name="email">The address to invite.</param>
    /// <param name="roleCode">The role offered.</param>
    /// <param name="appliesToDescendants">Whether the role applies below the organisation.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    Task<ManagementResult<InvitationSent>> CreateForApplicationAsync(Guid appId, Guid orgId, string email, string roleCode, bool appliesToDescendants, CancellationToken cancellationToken = default);

    /// <summary>The invitation behind a token, or <see langword="null"/> when the token is unknown.</summary>
    /// <param name="token">The token from the e-mail.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    Task<InvitationView?> GetAsync(string token, CancellationToken cancellationToken = default);

    /// <summary>Accepts the invitation for the signed-in person.</summary>
    /// <param name="token">The token from the e-mail.</param>
    /// <param name="userId">The signed-in person.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    Task<PartnerResult> AcceptAsync(string token, Guid userId, CancellationToken cancellationToken = default);
}
