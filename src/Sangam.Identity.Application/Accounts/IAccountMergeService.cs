namespace Sangam.Identity.Application.Accounts;

/// <summary>One of the two accounts, as the person sees it before merging (rc.7).</summary>
/// <param name="UserId">The account.</param>
/// <param name="Name">The name on the profile.</param>
/// <param name="Email">The account's e-mail address, shown in full: the person has just signed in to it.</param>
/// <param name="Verified">Whether it is verified with DigiLocker.</param>
/// <param name="Applications">The applications it is connected to, by name.</param>
/// <param name="AdminOf">The applications it administers, by name.</param>
public sealed record MergeAccountView(Guid UserId, string Name, string Email, bool Verified, IReadOnlyList<string> Applications, IReadOnlyList<string> AdminOf);

/// <summary>
/// What the closed account would lose in one application because the surviving account already holds the same thing
/// there (rc.7: the survivor's own wins).
/// </summary>
/// <param name="Application">The application, by name.</param>
/// <param name="Memberships">Roles at organisations the survivor is also a member of.</param>
/// <param name="Attributes">Values the application keeps that the survivor also has.</param>
public sealed record MergeLoss(string Application, int Memberships, int Attributes);

/// <summary>The two accounts side by side, and whether they may be merged (rc.7).</summary>
/// <param name="First">The account the person started from.</param>
/// <param name="Second">The account they signed in to second.</param>
/// <param name="Privileged">Whether either is privileged, so the wait is the longer one.</param>
/// <param name="WaitHours">The waiting period in hours.</param>
/// <param name="Refusal">
/// Why they may not be merged, or <see langword="null"/>: <c>same</c>, <c>inactive</c>, <c>operator</c>,
/// <c>different_identity</c>, <c>pending</c>.
/// </param>
/// <param name="LostIfFirstSurvives">What the second account would lose if the first stays.</param>
/// <param name="LostIfSecondSurvives">What the first account would lose if the second stays.</param>
public sealed record MergePreview(
    MergeAccountView First,
    MergeAccountView Second,
    bool Privileged,
    int WaitHours,
    string? Refusal,
    IReadOnlyList<MergeLoss> LostIfFirstSurvives,
    IReadOnlyList<MergeLoss> LostIfSecondSurvives);

/// <summary>The result of asking for a merge.</summary>
/// <param name="Succeeded">Whether the waiting period started.</param>
/// <param name="Refusal">Why not (as <see cref="MergePreview.Refusal"/>).</param>
/// <param name="EffectiveAt">When it takes effect.</param>
public sealed record MergeOutcome(bool Succeeded, string? Refusal, DateTimeOffset? EffectiveAt = null);

/// <summary>A merge waiting out its period.</summary>
/// <param name="RequestId">The request.</param>
/// <param name="SurvivorUserId">The account that stays.</param>
/// <param name="ClosedUserId">The account that is closed.</param>
/// <param name="SurvivorEmail">The surviving account's e-mail, masked.</param>
/// <param name="ClosedEmail">The closed account's e-mail, masked.</param>
/// <param name="RequestedAt">When it was asked for.</param>
/// <param name="EffectiveAt">When it takes effect.</param>
public sealed record PendingMerge(Guid RequestId, Guid SurvivorUserId, Guid ClosedUserId, string SurvivorEmail, string ClosedEmail, DateTimeOffset RequestedAt, DateTimeOffset EffectiveAt);

/// <summary>Where a closed account went.</summary>
/// <param name="UserId">The account it now lives on (followed to the end if that one was merged again).</param>
/// <param name="MergedAt">When it was merged.</param>
public sealed record MergedInto(Guid UserId, DateTimeOffset MergedAt);

/// <summary>
/// rc.7 (SGM-914 section 9): a person merges two Sangam accounts they hold — for example the one they could not recover
/// and the new one they made. Only the person can do it, signed in fully to both in one sitting; nobody at Sangam and no
/// application can merge someone's accounts. It waits 24 hours (72 when either account administers an application or
/// organisation) with a cancel link to both, then moves the closed account's connections to the surviving one, the
/// survivor's own winning where both hold the same thing. Operators' accounts are never merged this way.
/// </summary>
public interface IAccountMergeService
{
    /// <summary>Why this account may not start a merge, or <see langword="null"/>.</summary>
    /// <param name="userId">The account.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<string?> CanStartAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>The two accounts side by side, with the rules applied.</summary>
    /// <param name="firstUserId">The account the person started from.</param>
    /// <param name="secondUserId">The account they signed in to second.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<MergePreview> PreviewAsync(Guid firstUserId, Guid secondUserId, CancellationToken cancellationToken = default);

    /// <summary>Starts the waiting period, and tells both accounts with a link to cancel.</summary>
    /// <param name="survivorUserId">The account that stays.</param>
    /// <param name="closedUserId">The account that is closed into it.</param>
    /// <param name="ipAddress">The caller's address.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<MergeOutcome> RequestAsync(Guid survivorUserId, Guid closedUserId, string? ipAddress, CancellationToken cancellationToken = default);

    /// <summary>The merge waiting on this account, on either side, if any.</summary>
    /// <param name="userId">The account.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<PendingMerge?> PendingAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>The pending merge behind a cancel link, if it is still pending.</summary>
    /// <param name="token">The token from the link.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<PendingMerge?> FindByTokenAsync(string token, CancellationToken cancellationToken = default);

    /// <summary>Cancels with the link from the e-mail; false when it was no longer pending.</summary>
    /// <param name="token">The token.</param>
    /// <param name="ipAddress">The caller's address.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<bool> CancelByTokenAsync(string token, string? ipAddress, CancellationToken cancellationToken = default);

    /// <summary>Cancels from either account, signed in; false when nothing was pending.</summary>
    /// <param name="userId">The account.</param>
    /// <param name="ipAddress">The caller's address.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<bool> CancelByOwnerAsync(Guid userId, string? ipAddress, CancellationToken cancellationToken = default);

    /// <summary>Applies the merges whose waiting period has ended, each once; returns how many were applied.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<int> ApplyDueAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Where a closed account went, for an application that was connected to it — never for one that was not, so an
    /// application cannot learn about accounts it never had.
    /// </summary>
    /// <param name="appId">The calling application.</param>
    /// <param name="userId">The closed account's id (<c>sub</c>).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<MergedInto?> MergedIntoAsync(Guid appId, Guid userId, CancellationToken cancellationToken = default);
}
