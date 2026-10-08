using Sangam.Identity.Domain.Enums;

namespace Sangam.Identity.Domain.Entities;

/// <summary>
/// A grievance under the Digital Personal Data Protection Act, 2023 (D-D): logged by the grievance officer's team,
/// acknowledged within two working days and resolved within thirty days of receipt, every step kept.
/// </summary>
public class Grievance
{
    /// <summary>Row id.</summary>
    public Guid Id { get; set; }

    /// <summary>The reference given to the person, for example <c>GRV-2026-0007</c>.</summary>
    public string Reference { get; set; } = string.Empty;

    /// <summary>When Sangam received it (the clocks run from here).</summary>
    public DateTimeOffset ReceivedAt { get; set; }

    /// <summary>How it arrived: <c>email</c>, <c>letter</c>, <c>phone</c>, <c>in_person</c>, <c>other</c>.</summary>
    public string Channel { get; set; } = string.Empty;

    /// <summary>What it is about: <c>access</c>, <c>correction</c>, <c>erasure</c>, <c>consent</c>, <c>security</c>, <c>account</c>, <c>other</c>.</summary>
    public string Category { get; set; } = string.Empty;

    /// <summary>The person's name as they gave it.</summary>
    public string ComplainantName { get; set; } = string.Empty;

    /// <summary>How to reach them: an e-mail address or a mobile number.</summary>
    public string ComplainantContact { get; set; } = string.Empty;

    /// <summary>Their Sangam account, when it is known.</summary>
    public Guid? UserId { get; set; }

    /// <summary>The grievance in brief.</summary>
    public string Summary { get; set; } = string.Empty;

    /// <summary>Where it stands.</summary>
    public GrievanceStatus Status { get; set; }

    /// <summary>The acknowledgement deadline: two working days after receipt.</summary>
    public DateTimeOffset AcknowledgeBy { get; set; }

    /// <summary>The resolution deadline: thirty days after receipt.</summary>
    public DateTimeOffset ResolveBy { get; set; }

    /// <summary>When it was acknowledged.</summary>
    public DateTimeOffset? AcknowledgedAt { get; set; }

    /// <summary>When it was closed.</summary>
    public DateTimeOffset? ClosedAt { get; set; }

    /// <summary>The answer given to the person.</summary>
    public string? Resolution { get; set; }

    /// <summary>The operator who logged it.</summary>
    public Guid CreatedByUserId { get; set; }

    /// <summary>Every step, oldest first.</summary>
    public List<GrievanceEntry> Entries { get; set; } = [];
}

/// <summary>One step in a grievance's history: a note, an acknowledgement, a resolution.</summary>
public class GrievanceEntry
{
    /// <summary>Row id.</summary>
    public long Id { get; set; }

    /// <summary>The grievance.</summary>
    public Guid GrievanceId { get; set; }

    /// <summary>When.</summary>
    public DateTimeOffset At { get; set; }

    /// <summary>The operator.</summary>
    public Guid OperatorUserId { get; set; }

    /// <summary><c>logged</c>, <c>acknowledged</c>, <c>note</c>, <c>resolved</c>, <c>declined</c>.</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>What was done or said.</summary>
    public string Text { get; set; } = string.Empty;
}
