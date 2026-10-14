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

/// <summary>
/// Invitations by e-mail (PR-13). Since rc.12 no new invitations are made (people are added as staff, with an activation
/// code or their join code: <see cref="Tenancy.IStaffService"/>); links already sent keep working until they expire.
/// </summary>
public interface IInvitationService
{
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
