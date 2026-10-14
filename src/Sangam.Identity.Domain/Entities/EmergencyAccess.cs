namespace Sangam.Identity.Domain.Entities;

/// <summary>rc.12 (M12): how an emergency access reviewer judged one use.</summary>
public enum EmergencyVerdict
{
    /// <summary>The use was justified.</summary>
    Justified = 0,

    /// <summary>The use was not justified.</summary>
    NotJustified = 1,
}

/// <summary>
/// rc.12 (M12; founder decisions, 14 Oct 2026): one use of emergency access ("break-glass"). A member of the hospital's
/// staff gives a reason, proves it is them again with a passkey or authenticator app, and for up to 60 minutes the
/// application's tokens carry the emergency marker, the case reference and the end time. Sangam holds no patient data: the
/// case reference is the application's own opaque number. Each use is e-mailed to the organisation's reviewers, who mark it
/// justified or not.
/// </summary>
public sealed class EmergencyAccess
{
    /// <summary>Primary key.</summary>
    public Guid Id { get; set; }

    /// <summary>The application.</summary>
    public Guid AppId { get; set; }

    /// <summary>The organisation the person works at, where emergency access is switched on.</summary>
    public Guid OrgId { get; set; }

    /// <summary>The person.</summary>
    public Guid UserId { get; set; }

    /// <summary>The Sangam session it was started in.</summary>
    public Guid? SessionId { get; set; }

    /// <summary>The reason the person gave.</summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>The application's opaque reference to the case (never the patient's name or details).</summary>
    public string CaseRef { get; set; } = string.Empty;

    /// <summary>How identity was checked again: <c>passkey</c> or <c>totp</c>.</summary>
    public string Method { get; set; } = string.Empty;

    /// <summary>When it started.</summary>
    public DateTimeOffset StartedAt { get; set; }

    /// <summary>When it ends by itself (at most 60 minutes after the start).</summary>
    public DateTimeOffset EndsAt { get; set; }

    /// <summary>When the application ended it early.</summary>
    public DateTimeOffset? EndedAt { get; set; }

    /// <summary>Why the application ended it early.</summary>
    public string? EndReason { get; set; }

    /// <summary>When a reviewer judged it.</summary>
    public DateTimeOffset? ReviewedAt { get; set; }

    /// <summary>The reviewer.</summary>
    public Guid? ReviewedByUserId { get; set; }

    /// <summary>The reviewer's verdict.</summary>
    public EmergencyVerdict? Verdict { get; set; }

    /// <summary>The reviewer's note.</summary>
    public string? ReviewNote { get; set; }
}
