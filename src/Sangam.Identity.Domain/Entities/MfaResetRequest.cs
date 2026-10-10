namespace Sangam.Identity.Domain.Entities;

/// <summary>
/// A request to reset someone's two-step sign-in, waiting out its cooling-off period (D-K): 24 hours for ordinary
/// accounts, 72 for privileged ones. It is applied only when the period ends and nobody cancelled it, or at once under
/// the founder's urgent override. From rc.6 (SGM-914) it comes from the person's own recovery with DigiLocker, and a
/// record that does not match the account waits for an operator's review first.
/// </summary>
public class MfaResetRequest
{
    /// <summary>Identifier.</summary>
    public Guid Id { get; set; }

    /// <summary>The account whose second factor would be reset.</summary>
    public Guid UserId { get; set; }

    /// <summary>The operator who made the request.</summary>
    public Guid RequestedByUserId { get; set; }

    /// <summary>How the operator verified the person's identity (<c>video_call</c>, <c>in_person</c>, <c>verified_mobile_callback</c>).</summary>
    public string VerificationMethod { get; set; } = string.Empty;

    /// <summary>The support ticket or note — never an identity-document number.</summary>
    public string Reference { get; set; } = string.Empty;

    /// <summary>Whether the account is privileged (an operator, an application administrator or an organisation administrator).</summary>
    public bool Privileged { get; set; }

    /// <summary>When the request was made.</summary>
    public DateTimeOffset RequestedAt { get; set; }

    /// <summary>When the cooling-off period ends.</summary>
    public DateTimeOffset EffectiveAt { get; set; }

    /// <summary>SHA-256 of the owner's one-click cancel token (the token itself is only in the e-mail).</summary>
    public string CancelTokenHash { get; set; } = string.Empty;

    /// <summary>When it was cancelled, if it was.</summary>
    public DateTimeOffset? CancelledAt { get; set; }

    /// <summary>Who cancelled it: <c>owner</c> (the link or the sign-in notice) or <c>operator</c>.</summary>
    public string? CancelledBy { get; set; }

    /// <summary>When it was applied, if it was.</summary>
    public DateTimeOffset? AppliedAt { get; set; }

    /// <summary>The operator who applied it at once (urgent override), if one did.</summary>
    public Guid? UrgentByUserId { get; set; }

    /// <summary>The written reason for an urgent override.</summary>
    public string? UrgentReason { get; set; }

    /// <summary>When the owner saw the notice at sign-in.</summary>
    public DateTimeOffset? NoticeSeenAt { get; set; }

    /// <summary>
    /// rc.6 (SGM-914): an operator's review, for a recovery whose DigiLocker record did not match the account:
    /// <see langword="null"/> when none was needed, else <see cref="MfaResetReview"/>. A request waiting for review never
    /// takes effect.
    /// </summary>
    public string? ReviewStatus { get; set; }

    /// <summary>
    /// rc.6: what DigiLocker said (name, date of birth, gender, the keyed hash of the DigiLocker id) and which part did
    /// not match, as JSON — kept only until the request is applied, refused or cancelled, then cleared (SGM-910).
    /// </summary>
    public string? Record { get; set; }

    /// <summary>The operator who reviewed it.</summary>
    public Guid? ReviewedByUserId { get; set; }

    /// <summary>When it was reviewed.</summary>
    public DateTimeOffset? ReviewedAt { get; set; }

    /// <summary>The reviewer's reason.</summary>
    public string? ReviewReason { get; set; }

    /// <summary>Whether it is still waiting: neither cancelled nor applied.</summary>
    public bool Pending => CancelledAt is null && AppliedAt is null;
}
