namespace Sangam.Identity.Domain.Entities;

/// <summary>What an application administrator decided about one role in an access review (rc.11, R6).</summary>
public enum AccessReviewOutcome
{
    /// <summary>The role stays as it is; only the review is recorded.</summary>
    Keep = 0,

    /// <summary>The role was given a later end date.</summary>
    Extend = 1,

    /// <summary>The role was ended that day, with a reason.</summary>
    End = 2,
}

/// <summary>
/// rc.11 (R6; founder decision, 13 Oct 2026): one decision in an application's access review. The review lists roles
/// ending in the next 30 days and roles held by people who have not signed in to that application for 90 days; each is
/// kept, extended by 3, 6 or 12 months, or ended with a reason. A decided role leaves the list for that window.
/// </summary>
public sealed class AccessReviewDecision
{
    /// <summary>How long a role ending soon stays off the list once decided.</summary>
    public static readonly TimeSpan EndingWindow = TimeSpan.FromDays(30);

    /// <summary>How long without signing in to the application before a role is listed, and how long a decision about it holds.</summary>
    public static readonly TimeSpan UnusedWindow = TimeSpan.FromDays(90);

    /// <summary>Primary key.</summary>
    public Guid Id { get; set; }

    /// <summary>The application.</summary>
    public Guid AppId { get; set; }

    /// <summary>The role (organisation membership) decided about.</summary>
    public Guid MembershipId { get; set; }

    /// <summary>Who holds the role.</summary>
    public Guid MemberUserId { get; set; }

    /// <summary>The organisation.</summary>
    public Guid OrgId { get; set; }

    /// <summary>A role the person held there (rc.12: a review covers all of them; <see langword="null"/> when they hold
    /// none in Sangam).</summary>
    public Guid? RoleId { get; set; }

    /// <summary>Which list it was on: <c>ending</c> or <c>unused</c>.</summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>What was decided.</summary>
    public AccessReviewOutcome Outcome { get; set; }

    /// <summary>The new end date, for <see cref="AccessReviewOutcome.Extend"/>.</summary>
    public DateTimeOffset? NewExpiresAt { get; set; }

    /// <summary>Why the role was ended, for <see cref="AccessReviewOutcome.End"/>.</summary>
    public string? Note { get; set; }

    /// <summary>The administrator who decided.</summary>
    public Guid DecidedByUserId { get; set; }

    /// <summary>When.</summary>
    public DateTimeOffset DecidedAt { get; set; }
}
