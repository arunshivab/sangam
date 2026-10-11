namespace Sangam.Identity.Domain.Entities;

/// <summary>
/// rc.7 (SGM-914 section 9): a person's request to merge two Sangam accounts they hold, made after signing in fully to
/// both. It waits 24 hours (72 when either account is privileged) with a cancel link sent to both, then the closed
/// account's applications, roles, attributes, consents and verified identity move to the surviving account — the
/// survivor's own wins where both hold the same thing — and the closed account is pseudonymised. Applied requests are
/// kept, with the two ids only, so that the closed id can still answer "merged into" to an application that knew it.
/// </summary>
public class AccountMergeRequest
{
    /// <summary>Identifier.</summary>
    public Guid Id { get; set; }

    /// <summary>The account that stays.</summary>
    public Guid SurvivorUserId { get; set; }

    /// <summary>The account that is closed into it.</summary>
    public Guid ClosedUserId { get; set; }

    /// <summary>Whether either account is privileged (an application or organisation administrator): the wait is 72 hours.</summary>
    public bool Privileged { get; set; }

    /// <summary>When it was asked for.</summary>
    public DateTimeOffset RequestedAt { get; set; }

    /// <summary>When the waiting period ends.</summary>
    public DateTimeOffset EffectiveAt { get; set; }

    /// <summary>SHA-256 of the one-click cancel token (the token itself is only in the e-mails).</summary>
    public string CancelTokenHash { get; set; } = string.Empty;

    /// <summary>When it was cancelled, if it was.</summary>
    public DateTimeOffset? CancelledAt { get; set; }

    /// <summary>
    /// Why it ended without merging: <c>owner</c> (the link or either account), <c>refused</c> (a rule failed when it was
    /// due, for example an account was suspended in the meantime).
    /// </summary>
    public string? CancelledBy { get; set; }

    /// <summary>When it was applied, if it was.</summary>
    public DateTimeOffset? AppliedAt { get; set; }
}
