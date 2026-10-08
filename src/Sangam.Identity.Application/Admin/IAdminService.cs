using Sangam.Identity.Domain.Enums;

namespace Sangam.Identity.Application.Admin;

/// <summary>
/// What a platform operator may do from the admin console. Every method takes the acting
/// operator's id: authorisation is decided here, not in the UI, and every call is audited —
/// reads of a user's record included.
/// <para>
/// There is deliberately no method to sign in as a user. Impersonation is not a feature Sangam
/// will have: in a clinical setting it would let an operator act inside a hospital application
/// as a doctor, which is a patient-record integrity problem, not merely a security one.
/// </para>
/// </summary>
public interface IAdminService
{
    /// <summary>The rank the operator holds, or <see langword="null"/> when they hold none.</summary>
    /// <param name="operatorUserId">The signed-in operator.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<PlatformRole?> GetRoleAsync(Guid operatorUserId, CancellationToken cancellationToken = default);

    /// <summary>Finds users by name, email or mobile. Searching is not audited; opening a record is.</summary>
    /// <param name="operatorUserId">The signed-in operator.</param>
    /// <param name="query">Search text; empty lists the most recent accounts.</param>
    /// <param name="take">Maximum rows.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IReadOnlyList<AdminUserRow>> SearchUsersAsync(Guid operatorUserId, string? query, int take, CancellationToken cancellationToken = default);

    /// <summary>Opens a user's record, writing an audit row the user can see in their own log.</summary>
    /// <param name="operatorUserId">The signed-in operator.</param>
    /// <param name="userId">The user being looked at.</param>
    /// <param name="ipAddress">Client IP.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<AdminUserDetail?> OpenUserAsync(Guid operatorUserId, Guid userId, string? ipAddress, CancellationToken cancellationToken = default);

    /// <summary>Suspends an account, ending its sessions.</summary>
    Task<AdminResult> SuspendUserAsync(Guid operatorUserId, Guid userId, string reason, string? ipAddress, CancellationToken cancellationToken = default);

    /// <summary>Lifts a suspension.</summary>
    Task<AdminResult> ReinstateUserAsync(Guid operatorUserId, Guid userId, string? ipAddress, CancellationToken cancellationToken = default);

    /// <summary>Ends every session the user holds.</summary>
    Task<AdminResult> ForceSignOutAsync(Guid operatorUserId, Guid userId, string? ipAddress, CancellationToken cancellationToken = default);

    /// <summary>Places a hold that blocks the scheduled purge of a deleted account.</summary>
    Task<AdminResult> PlaceHoldAsync(Guid operatorUserId, Guid userId, string reason, string? ipAddress, CancellationToken cancellationToken = default);

    /// <summary>Clears a hold, so the purge may run again.</summary>
    Task<AdminResult> ClearHoldAsync(Guid operatorUserId, Guid userId, string? ipAddress, CancellationToken cancellationToken = default);

    /// <summary>Deletes an account immediately, without waiting out the grace period.</summary>
    Task<AdminResult> DeleteNowAsync(Guid operatorUserId, Guid userId, string reason, string? ipAddress, CancellationToken cancellationToken = default);

    /// <summary>Lists the applications in the registry.</summary>
    Task<IReadOnlyList<AdminAppRow>> ListAppsAsync(Guid operatorUserId, CancellationToken cancellationToken = default);

    /// <summary>Enables or disables an application, which stops or resumes every sign-in to it.</summary>
    Task<AdminResult> SetAppStatusAsync(Guid operatorUserId, Guid appId, AppStatus status, string? ipAddress, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets where an application is told that a person's Sangam session has ended (PR-20): a back-channel address
    /// that receives a signed logout token, and a front-channel page loaded in a hidden frame. Either may be blank
    /// for none. AppManager and above; each must be an absolute https address (http only for localhost).
    /// </summary>
    Task<AdminResult> SetAppLogoutUrisAsync(Guid operatorUserId, Guid appId, string? backChannelLogoutUri, string? frontChannelLogoutUri, string? ipAddress, CancellationToken cancellationToken = default);

    /// <summary>
    /// Makes someone an owner of an application, so its partner can manage it on the partner console.
    /// AppManager and above. Unlike an owner adding an administrator, the person need not have linked
    /// the application yet: this is the platform operators' own act, done when a partner is onboarded.
    /// </summary>
    Task<AdminResult> AssignAppOwnerAsync(Guid operatorUserId, Guid appId, string email, string? ipAddress, CancellationToken cancellationToken = default);

    /// <summary>Lists console operators. Owners only.</summary>
    Task<IReadOnlyList<OperatorRow>> ListOperatorsAsync(Guid operatorUserId, CancellationToken cancellationToken = default);

    /// <summary>Grants console access to an existing Sangam account. Owners only.</summary>
    Task<AdminResult> GrantOperatorAsync(Guid operatorUserId, string email, PlatformRole role, string? ipAddress, CancellationToken cancellationToken = default);

    /// <summary>Revokes console access. Owners only, and never the last owner.</summary>
    Task<AdminResult> RevokeOperatorAsync(Guid operatorUserId, Guid userId, string? ipAddress, CancellationToken cancellationToken = default);

    /// <summary>
    /// Asks to reset a person's two-step sign-in after support has verified who they are (PR-16, CAP-019, D-K). Nothing
    /// happens at once: the owner is alerted on every channel with a one-click cancel link, and the reset is applied
    /// only after the cooling-off period (24 hours; 72 for operators and application or organisation administrators)
    /// if nobody cancelled it. Support or above; an operator's own second factor only by an Owner, and never your own.
    /// The verification method is required and audited; the reference is a ticket number or note — never an
    /// identity-document number.
    /// </summary>
    /// <param name="operatorUserId">The operator.</param>
    /// <param name="userId">The person.</param>
    /// <param name="method">How their identity was verified.</param>
    /// <param name="reference">The support ticket or note.</param>
    /// <param name="ipAddress">Operator's IP.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<AdminResult> RequestTwoStepResetAsync(Guid operatorUserId, Guid userId, IdentityProofingMethod method, string reference, string? ipAddress, CancellationToken cancellationToken = default);

    /// <summary>
    /// Urgent override (D-K): applies a two-step reset at once, skipping the cooling-off period. Support or Owner rank,
    /// a written reason, its own audit event, and an immediate alert to the platform's owner.
    /// </summary>
    /// <param name="operatorUserId">The operator.</param>
    /// <param name="userId">The person.</param>
    /// <param name="method">How their identity was verified (used when no request is pending yet).</param>
    /// <param name="reference">The support ticket or note (used when no request is pending yet).</param>
    /// <param name="reason">Why it cannot wait.</param>
    /// <param name="ipAddress">Operator's IP.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<AdminResult> ApplyTwoStepResetNowAsync(Guid operatorUserId, Guid userId, IdentityProofingMethod method, string reference, string reason, string? ipAddress, CancellationToken cancellationToken = default);

    /// <summary>Withdraws a pending two-step reset (Support or above).</summary>
    /// <param name="operatorUserId">The operator.</param>
    /// <param name="userId">The person.</param>
    /// <param name="ipAddress">Operator's IP.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<AdminResult> WithdrawTwoStepResetAsync(Guid operatorUserId, Guid userId, string? ipAddress, CancellationToken cancellationToken = default);

    /// <summary>The pending two-step reset on an account, if any, for the console.</summary>
    /// <param name="userId">The person.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<Accounts.PendingTwoStepReset?> PendingTwoStepResetAsync(Guid userId, CancellationToken cancellationToken = default);
}
