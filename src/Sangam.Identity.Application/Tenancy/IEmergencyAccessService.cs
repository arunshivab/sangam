using Sangam.Identity.Domain.Entities;

namespace Sangam.Identity.Application.Tenancy;

/// <summary>rc.12 (M12): what the person asks for on the emergency access screen.</summary>
/// <param name="UserId">The person.</param>
/// <param name="AppId">The application.</param>
/// <param name="OrgId">The organisation they are working at.</param>
/// <param name="SessionId">Their Sangam session.</param>
/// <param name="CaseRef">The application's opaque case reference.</param>
/// <param name="Reason">Why.</param>
/// <param name="Method">How identity was checked again just now: <c>passkey</c> or <c>totp</c>.</param>
public sealed record EmergencyRequest(Guid UserId, Guid AppId, Guid OrgId, Guid? SessionId, string CaseRef, string Reason, string Method);

/// <summary>rc.12 (M12): outcome of asking for emergency access.</summary>
public enum EmergencyStartStatus
{
    /// <summary>Started: it ends by itself after 60 minutes.</summary>
    Started = 0,

    /// <summary>The person is not staff of that organisation in this application.</summary>
    NotStaff = 1,

    /// <summary>The organisation has not switched emergency access on.</summary>
    SwitchedOff = 2,

    /// <summary>Three uses today already.</summary>
    DailyLimit = 3,

    /// <summary>No reason was given.</summary>
    ReasonRequired = 4,

    /// <summary>The application sent no usable case reference.</summary>
    CaseRequired = 5,
}

/// <summary>rc.12 (M12): one use of emergency access in progress.</summary>
/// <param name="Id">The use.</param>
/// <param name="OrgId">The organisation.</param>
/// <param name="CaseRef">The case reference.</param>
/// <param name="StartedAt">When it started.</param>
/// <param name="EndsAt">When it ends by itself.</param>
public sealed record EmergencyGrant(Guid Id, Guid OrgId, string CaseRef, DateTimeOffset StartedAt, DateTimeOffset EndsAt);

/// <summary>rc.12 (M12): outcome of asking.</summary>
/// <param name="Status">What happened.</param>
/// <param name="Grant">The use, when started.</param>
/// <param name="UsedToday">Uses today, including this one.</param>
public sealed record EmergencyStartResult(EmergencyStartStatus Status, EmergencyGrant? Grant = null, int UsedToday = 0);

/// <summary>rc.12 (M12): a use in the emergency access log.</summary>
/// <param name="Id">The use.</param>
/// <param name="Person">Who.</param>
/// <param name="UserId">Their account.</param>
/// <param name="OrgName">Where.</param>
/// <param name="CaseRef">The application's case reference.</param>
/// <param name="Reason">Why.</param>
/// <param name="Method">How identity was checked again.</param>
/// <param name="StartedAt">Start.</param>
/// <param name="EndedAt">End (early, or by itself).</param>
/// <param name="EndReason">Why the application ended it early.</param>
/// <param name="Verdict">The reviewer's verdict.</param>
/// <param name="ReviewNote">The reviewer's note.</param>
/// <param name="ReviewedBy">The reviewer.</param>
/// <param name="ReviewedAt">When reviewed.</param>
/// <param name="MayReview">Whether the person looking may judge it (a reviewer there, and not their own use).</param>
public sealed record EmergencyUse(
    Guid Id,
    string Person,
    Guid UserId,
    string OrgName,
    string CaseRef,
    string Reason,
    string Method,
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    string? EndReason,
    EmergencyVerdict? Verdict,
    string? ReviewNote,
    string? ReviewedBy,
    DateTimeOffset? ReviewedAt,
    bool MayReview);

/// <summary>rc.12 (M12): one customer's emergency access uses waiting for review, for the application's access review.</summary>
/// <param name="CustomerId">The customer.</param>
/// <param name="CustomerName">Its name.</param>
/// <param name="Fresh">Waiting under 24 hours.</param>
/// <param name="Due">Waiting 24 to 72 hours.</param>
/// <param name="Late">Waiting over 72 hours.</param>
public sealed record EmergencyWaiting(Guid CustomerId, string CustomerName, int Fresh, int Due, int Late);

/// <summary>rc.12 (M12): how urgently a use waits for review.</summary>
public enum EmergencyReviewAge
{
    /// <summary>Reviewed.</summary>
    Reviewed = 0,

    /// <summary>Under 24 hours unreviewed (green).</summary>
    Fresh = 1,

    /// <summary>24 to 72 hours unreviewed (orange).</summary>
    Due = 2,

    /// <summary>Over 72 hours unreviewed (red).</summary>
    Late = 3,
}

/// <summary>
/// rc.12 (M12; founder decisions, 14 Oct 2026): emergency access ("break-glass"), the same for every application. Only the
/// hospital's own staff, only where the hospital switched it on, with a reason and a fresh passkey or authenticator check;
/// up to 60 minutes; at most 3 uses per person per day; recorded and e-mailed to the organisation's reviewers at once; each
/// use reviewed as justified or not.
/// </summary>
public interface IEmergencyAccessService
{
    /// <summary>The most uses per person per day.</summary>
    const int DailyLimit = 3;

    /// <summary>The longest one use lasts.</summary>
    static readonly TimeSpan Longest = TimeSpan.FromMinutes(60);

    /// <summary>Whether the organisation (or one above it) has switched emergency access on.</summary>
    /// <param name="appId">The application.</param>
    /// <param name="orgId">The organisation.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><see langword="true"/> when on.</returns>
    Task<bool> IsOnAsync(Guid appId, Guid orgId, CancellationToken cancellationToken = default);

    /// <summary>Starts emergency access.</summary>
    /// <param name="request">What was asked.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The outcome.</returns>
    Task<EmergencyStartResult> StartAsync(EmergencyRequest request, CancellationToken cancellationToken = default);

    /// <summary>The person's use in progress for this application, session and case, if any.</summary>
    /// <param name="userId">The person.</param>
    /// <param name="appId">The application.</param>
    /// <param name="sessionId">The session.</param>
    /// <param name="caseRef">The case reference.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The use, or <see langword="null"/>.</returns>
    Task<EmergencyGrant?> ActiveAsync(Guid userId, Guid appId, Guid? sessionId, string caseRef, CancellationToken cancellationToken = default);

    /// <summary>A use, while still in progress (for tokens refreshed during it).</summary>
    /// <param name="id">The use.</param>
    /// <param name="userId">The person.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The use, or <see langword="null"/> once it ended.</returns>
    Task<EmergencyGrant?> ActiveByIdAsync(Guid id, Guid userId, CancellationToken cancellationToken = default);

    /// <summary>The application ends the person's use early, with a reason.</summary>
    /// <param name="userId">The person.</param>
    /// <param name="appId">The application.</param>
    /// <param name="sessionId">The session.</param>
    /// <param name="reason">Why (the patient left the ward, care handed over, the emergency is over).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><see langword="false"/> when nothing was in progress.</returns>
    Task<bool> EndAsync(Guid userId, Guid appId, Guid? sessionId, string reason, CancellationToken cancellationToken = default);

    /// <summary>Uses in an organisation and everything under it, newest first, for its admins and reviewers.</summary>
    /// <param name="viewerUserId">The person looking.</param>
    /// <param name="appId">The application.</param>
    /// <param name="orgId">The organisation.</param>
    /// <param name="days">How far back.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The uses, or <see langword="null"/> when the person may not see them.</returns>
    Task<IReadOnlyList<EmergencyUse>?> LogAsync(Guid viewerUserId, Guid appId, Guid orgId, int days = 90, CancellationToken cancellationToken = default);

    /// <summary>For an application's admins: each customer's uses still waiting for review, by how long they have waited.</summary>
    /// <param name="appAdminUserId">An administrator of the application.</param>
    /// <param name="appId">The application.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The customers with uses waiting, most overdue first; empty for anyone else.</returns>
    Task<IReadOnlyList<EmergencyWaiting>> WaitingAsync(Guid appAdminUserId, Guid appId, CancellationToken cancellationToken = default);

    /// <summary>A reviewer marks a use justified or not.</summary>
    /// <param name="reviewerUserId">The reviewer.</param>
    /// <param name="appId">The application.</param>
    /// <param name="useId">The use.</param>
    /// <param name="verdict">The verdict.</param>
    /// <param name="note">A note (required when not justified).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><see langword="false"/> when the person may not judge it, or a note is missing.</returns>
    Task<bool> ReviewAsync(Guid reviewerUserId, Guid appId, Guid useId, EmergencyVerdict verdict, string? note, CancellationToken cancellationToken = default);

    /// <summary>How urgently an unreviewed use waits: green under 24 hours, orange to 72, red after.</summary>
    /// <param name="startedAt">When the use started.</param>
    /// <param name="reviewed">Whether it was reviewed.</param>
    /// <param name="now">Now.</param>
    /// <returns>The age.</returns>
    static EmergencyReviewAge AgeOf(DateTimeOffset startedAt, bool reviewed, DateTimeOffset now)
        => reviewed ? EmergencyReviewAge.Reviewed
            : now - startedAt < TimeSpan.FromHours(24) ? EmergencyReviewAge.Fresh
            : now - startedAt < TimeSpan.FromHours(72) ? EmergencyReviewAge.Due
            : EmergencyReviewAge.Late;
}
