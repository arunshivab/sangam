using Sangam.Identity.Application.Admin;
using Sangam.Identity.Domain.Enums;

namespace Sangam.Identity.Application.Grievances;

/// <summary>
/// The grievance log in the operator console (D-D). Any operator may read it; Support and Owner may log and act.
/// Every step is audited and kept in the grievance's history; the deadlines (two working days to acknowledge,
/// thirty days to resolve) are set when it is logged.
/// </summary>
public interface IGrievanceService
{
    /// <summary>Grievances, open ones first (most urgent first), then the closed ones, newest first.</summary>
    /// <param name="operatorUserId">The operator.</param>
    /// <param name="openOnly">Only the open ones.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IReadOnlyList<GrievanceRow>?> ListAsync(Guid operatorUserId, bool openOnly, CancellationToken cancellationToken = default);

    /// <summary>One grievance with its history, or <see langword="null"/>.</summary>
    /// <param name="operatorUserId">The operator.</param>
    /// <param name="id">The grievance.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<GrievanceDetail?> GetAsync(Guid operatorUserId, Guid id, CancellationToken cancellationToken = default);

    /// <summary>Logs a new grievance; the result's message carries its reference.</summary>
    /// <param name="operatorUserId">The operator.</param>
    /// <param name="input">What was received.</param>
    /// <param name="ipAddress">Operator's IP address, for the audit.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<(AdminResult Result, Guid? Id)> LogAsync(Guid operatorUserId, GrievanceInput input, string? ipAddress, CancellationToken cancellationToken = default);

    /// <summary>Acknowledges it; when the contact is an e-mail address, the person is sent the acknowledgement.</summary>
    /// <param name="operatorUserId">The operator.</param>
    /// <param name="id">The grievance.</param>
    /// <param name="sendEmail">Send the acknowledgement by e-mail.</param>
    /// <param name="ipAddress">Operator's IP address.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<AdminResult> AcknowledgeAsync(Guid operatorUserId, Guid id, bool sendEmail, string? ipAddress, CancellationToken cancellationToken = default);

    /// <summary>Adds a note to its history.</summary>
    /// <param name="operatorUserId">The operator.</param>
    /// <param name="id">The grievance.</param>
    /// <param name="text">The note.</param>
    /// <param name="ipAddress">Operator's IP address.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<AdminResult> AddNoteAsync(Guid operatorUserId, Guid id, string text, string? ipAddress, CancellationToken cancellationToken = default);

    /// <summary>Closes it with the answer given to the person (resolved or declined); optionally e-mails the answer.</summary>
    /// <param name="operatorUserId">The operator.</param>
    /// <param name="id">The grievance.</param>
    /// <param name="outcome"><see cref="GrievanceStatus.Resolved"/> or <see cref="GrievanceStatus.Declined"/>.</param>
    /// <param name="resolution">The answer.</param>
    /// <param name="sendEmail">Send the answer by e-mail.</param>
    /// <param name="ipAddress">Operator's IP address.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<AdminResult> CloseAsync(Guid operatorUserId, Guid id, GrievanceStatus outcome, string resolution, bool sendEmail, string? ipAddress, CancellationToken cancellationToken = default);
}

/// <summary>What was received.</summary>
/// <param name="ReceivedAt">When Sangam received it (may be earlier than now, for a letter opened late).</param>
/// <param name="Channel">How it arrived.</param>
/// <param name="Category">What it is about.</param>
/// <param name="Name">The person's name.</param>
/// <param name="Contact">Their e-mail address or mobile number.</param>
/// <param name="Summary">The grievance in brief.</param>
public sealed record GrievanceInput(DateTimeOffset ReceivedAt, string Channel, string Category, string Name, string Contact, string Summary);

/// <summary>A grievance in the list.</summary>
/// <param name="Id">Row id.</param>
/// <param name="Reference">Reference.</param>
/// <param name="ReceivedAt">Received.</param>
/// <param name="Category">Category.</param>
/// <param name="Name">The person.</param>
/// <param name="Status">Status.</param>
/// <param name="AcknowledgeBy">Acknowledgement deadline.</param>
/// <param name="ResolveBy">Resolution deadline.</param>
/// <param name="AcknowledgeOverdue">Not acknowledged, past the deadline.</param>
/// <param name="ResolveOverdue">Still open, past the deadline.</param>
public sealed record GrievanceRow(Guid Id, string Reference, DateTimeOffset ReceivedAt, string Category, string Name, GrievanceStatus Status, DateTimeOffset AcknowledgeBy, DateTimeOffset ResolveBy, bool AcknowledgeOverdue, bool ResolveOverdue);

/// <summary>A grievance with its history.</summary>
/// <param name="Row">The list row.</param>
/// <param name="Channel">How it arrived.</param>
/// <param name="Contact">The person's contact.</param>
/// <param name="AccountEmail">Their Sangam account's e-mail address, when the contact matches one.</param>
/// <param name="Summary">The grievance.</param>
/// <param name="Resolution">The answer, once closed.</param>
/// <param name="History">Every step, oldest first.</param>
/// <param name="CanAct">Whether this operator may act on it.</param>
public sealed record GrievanceDetail(GrievanceRow Row, string Channel, string Contact, string? AccountEmail, string Summary, string? Resolution, IReadOnlyList<GrievanceStep> History, bool CanAct);

/// <summary>One step in the history.</summary>
/// <param name="At">When.</param>
/// <param name="Operator">Who (display name).</param>
/// <param name="Kind">What kind of step.</param>
/// <param name="Text">What was done or said.</param>
public sealed record GrievanceStep(DateTimeOffset At, string Operator, string Kind, string Text);

/// <summary>The fixed lists the console offers.</summary>
public static class GrievanceLists
{
    /// <summary>Channels.</summary>
    public static IReadOnlyList<string> Channels { get; } = ["email", "letter", "phone", "in_person", "other"];

    /// <summary>Categories.</summary>
    public static IReadOnlyList<string> Categories { get; } = ["access", "correction", "erasure", "consent", "security", "account", "other"];
}
