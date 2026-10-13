namespace Sangam.Identity.Application.Partners;

/// <summary>
/// rc.11 (R6): an application's access review on the partner console. Any administrator of the application may review; every
/// decision is recorded and audited, and an ended role's reason is shown to the person in their activity.
/// </summary>
public interface IAccessReviewService
{
    /// <summary>The review, or <c>null</c> when <paramref name="userId"/> does not administer <paramref name="appId"/>.</summary>
    /// <param name="userId">The administrator.</param>
    /// <param name="appId">The application.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<AccessReview?> GetAsync(Guid userId, Guid appId, CancellationToken cancellationToken = default);

    /// <summary>How many roles are waiting for a decision (for the sidebar).</summary>
    /// <param name="userId">The administrator.</param>
    /// <param name="appId">The application.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<int> CountAsync(Guid userId, Guid appId, CancellationToken cancellationToken = default);

    /// <summary>Records a decision and applies it.</summary>
    /// <param name="userId">The administrator.</param>
    /// <param name="appId">The application.</param>
    /// <param name="decision">The decision.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<PartnerResult> DecideAsync(Guid userId, Guid appId, AccessReviewInput decision, CancellationToken cancellationToken = default);
}

/// <summary>An access review.</summary>
/// <param name="Ending">Roles ending in the next 30 days.</param>
/// <param name="Unused">Roles of people who have not signed in to the application for 90 days.</param>
/// <param name="Decided">The latest decisions, newest first.</param>
public sealed record AccessReview(IReadOnlyList<AccessReviewItem> Ending, IReadOnlyList<AccessReviewItem> Unused, IReadOnlyList<AccessReviewDecided> Decided);

/// <summary>A role waiting for a decision.</summary>
/// <param name="MembershipId">The role (membership).</param>
/// <param name="Person">The person's name.</param>
/// <param name="Email">Their e-mail address.</param>
/// <param name="Organisation">The organisation.</param>
/// <param name="Role">The role, as the application named it.</param>
/// <param name="GrantedAt">When it was given.</param>
/// <param name="ExpiresAt">When it ends, if it does.</param>
/// <param name="LastSignInAt">When they last signed in to the application, if ever.</param>
public sealed record AccessReviewItem(Guid MembershipId, string Person, string Email, string Organisation, string Role, DateTimeOffset GrantedAt, DateTimeOffset? ExpiresAt, DateTimeOffset? LastSignInAt);

/// <summary>A decision taken.</summary>
/// <param name="DecidedAt">When.</param>
/// <param name="Person">Whose role.</param>
/// <param name="Organisation">The organisation.</param>
/// <param name="Role">The role.</param>
/// <param name="Outcome">Keep, Extend or End.</param>
/// <param name="NewExpiresAt">The new end date, when extended.</param>
/// <param name="Note">The reason, when ended.</param>
/// <param name="DecidedBy">Who decided.</param>
public sealed record AccessReviewDecided(DateTimeOffset DecidedAt, string Person, string Organisation, string Role, Domain.Entities.AccessReviewOutcome Outcome, DateTimeOffset? NewExpiresAt, string? Note, string DecidedBy);

/// <summary>A decision to record.</summary>
/// <param name="MembershipId">The role (membership).</param>
/// <param name="Outcome">Keep, Extend or End.</param>
/// <param name="ExtendMonths">3, 6 or 12, for Extend.</param>
/// <param name="Note">Why, for End (required).</param>
public sealed record AccessReviewInput(Guid MembershipId, Domain.Entities.AccessReviewOutcome Outcome, int? ExtendMonths, string? Note);
