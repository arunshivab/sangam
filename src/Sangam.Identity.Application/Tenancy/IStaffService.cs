namespace Sangam.Identity.Application.Tenancy;

/// <summary>
/// rc.12: who is adding staff. Always a person (an application admin, or a customer or branch admin), working in a Sangam
/// console or inside the application, which calls the management API with their identity. Every action is recorded under
/// their own name.
/// </summary>
/// <param name="AppId">The application whose organisations are being changed.</param>
/// <param name="AdminUserId">The admin.</param>
/// <param name="ViaApi">Whether the application called on the admin's behalf (rather than a Sangam console).</param>
public sealed record StaffActor(Guid AppId, Guid AdminUserId, bool ViaApi = false);

/// <summary>What an admin enters about a person (4.2 of the rc.12 design).</summary>
/// <param name="FirstName">First name.</param>
/// <param name="LastName">Last name.</param>
/// <param name="Email">Email; the activation link is sent here.</param>
/// <param name="Mobile">Mobile in E.164 form (<c>+91…</c>), if known.</param>
/// <param name="DateOfBirth">Date of birth.</param>
public sealed record StaffDetails(string FirstName, string LastName, string Email, string? Mobile, DateOnly DateOfBirth);

/// <summary>What the check before adding found (4.3).</summary>
public enum StaffCheckOutcome
{
    /// <summary>Nothing similar: a new account.</summary>
    None,

    /// <summary>An account with this email or mobile, and every detail matches.</summary>
    Match,

    /// <summary>An account with this email or mobile, but some details differ.</summary>
    Differs,

    /// <summary>No account with this email or mobile, but one with the same name and date of birth.</summary>
    Similar,
}

/// <summary>
/// The answer to a check: which details match and which differ, never what Sangam holds. The field names are
/// <c>first_name</c>, <c>last_name</c>, <c>date_of_birth</c>, <c>email</c> and <c>mobile</c>.
/// </summary>
/// <param name="Outcome">What was found.</param>
/// <param name="Matching">The fields that match an existing account.</param>
/// <param name="Differing">The fields that differ from it.</param>
/// <param name="Message">What to tell the admin, in English (the consoles translate it from the outcome and fields).</param>
/// <param name="ChecksLeftToday">How many more checks this admin may run about this person today.</param>
public sealed record StaffCheckResult(StaffCheckOutcome Outcome, IReadOnlyList<string> Matching, IReadOnlyList<string> Differing, string Message, int ChecksLeftToday);

/// <summary>
/// One addition: one person to one organisation (one call per organisation). Exactly one of <paramref name="Details"/>,
/// <paramref name="JoinCode"/>, <paramref name="StaffRef"/> and <paramref name="UserId"/> says who.
/// </summary>
/// <param name="Details">A person the admin enters (push): checked, then given an activation code.</param>
/// <param name="JoinCode">The join code the person made in their portal (pull).</param>
/// <param name="StaffRef">The reference an earlier addition returned, so the person is not entered (and checked, and
/// e-mailed) again for another organisation.</param>
/// <param name="UserId">Someone already in this customer in this application.</param>
/// <param name="Roles">Role codes at this organisation; empty for "staff" (or when the customer keeps roles in the
/// application).</param>
/// <param name="StartsOn">The first day of access (India time), for a joiner who starts later.</param>
/// <param name="ExpiresAt">When the roles end, if they do.</param>
/// <param name="Confirmed">The admin has seen the check's warning and proceeds anyway.</param>
public sealed record StaffAddition(
    StaffDetails? Details = null,
    string? JoinCode = null,
    Guid? StaffRef = null,
    Guid? UserId = null,
    IReadOnlyList<string>? Roles = null,
    DateOnly? StartsOn = null,
    DateTimeOffset? ExpiresAt = null,
    bool Confirmed = false);

/// <summary>How an addition went.</summary>
public enum StaffAddStatus
{
    /// <summary>The person is in the organisation now.</summary>
    Added,

    /// <summary>Waiting: for the person to activate with the admin's code, or for the start date.</summary>
    Waiting,

    /// <summary>The check found something the admin must confirm first (<see cref="StaffAddResult.Check"/>).</summary>
    NeedsConfirmation,

    /// <summary>Not allowed, or the input is wrong (<see cref="StaffAddResult.Message"/> says why).</summary>
    Refused,

    /// <summary>The organisation, code, reference or person was not found.</summary>
    NotFound,

    /// <summary>Too many checks or wrong codes; try again later.</summary>
    TooMany,
}

/// <summary>The result of one addition.</summary>
/// <param name="Status">How it went.</param>
/// <param name="Message">What to tell the admin.</param>
/// <param name="StaffRef">The reference to send for this person's other organisations, while they have not activated.</param>
/// <param name="UserId">The person's Sangam id, once they have joined (it is never given for someone who has not).</param>
/// <param name="ActivationCode">The code to hand to the person, shown once; only when this call made it.</param>
/// <param name="Check">The check's answer, when it was run.</param>
/// <param name="AddedBeforeDigiLocker">Whether the admin added under the application's temporary DigiLocker exception.</param>
public sealed record StaffAddResult(
    StaffAddStatus Status,
    string Message,
    Guid? StaffRef = null,
    Guid? UserId = null,
    string? ActivationCode = null,
    StaffCheckResult? Check = null,
    bool AddedBeforeDigiLocker = false);

/// <summary>Where an activation link stands.</summary>
public enum StaffInviteState
{
    /// <summary>Waiting for the code.</summary>
    Open,

    /// <summary>Already used.</summary>
    Accepted,

    /// <summary>Past its 14 days.</summary>
    Expired,

    /// <summary>Cancelled by an admin, or after five wrong codes.</summary>
    Cancelled,
}

/// <summary>What the person sees on opening the link.</summary>
/// <param name="State">Where it stands.</param>
/// <param name="FirstName">The first name the admin entered, for the greeting.</param>
/// <param name="MaskedEmail">The address it was sent to, masked.</param>
/// <param name="ForExistingAccount">Whether the person joins with an account they already have.</param>
/// <param name="Places">Each application and the organisations the person is being added to there.</param>
/// <param name="AddedBy">The admin's name.</param>
public sealed record StaffInviteView(StaffInviteState State, string FirstName, string MaskedEmail, bool ForExistingAccount, IReadOnlyList<StaffInvitePlace> Places, string AddedBy);

/// <summary>One application and its organisations, on the activation page.</summary>
/// <param name="Application">The application.</param>
/// <param name="Organisations">The organisations there.</param>
public sealed record StaffInvitePlace(string Application, IReadOnlyList<string> Organisations);

/// <summary>The answer to an activation code.</summary>
public enum StaffCodeStatus
{
    /// <summary>Right; the person may go on.</summary>
    Right,

    /// <summary>Wrong; <see cref="StaffCodeResult.AttemptsLeft"/> more tries.</summary>
    Wrong,

    /// <summary>Five wrong codes: the link no longer works.</summary>
    Locked,

    /// <summary>The link is used, expired or cancelled.</summary>
    NotOpen,
}

/// <summary>The answer to an activation code.</summary>
/// <param name="Status">Right or not.</param>
/// <param name="AttemptsLeft">Tries left.</param>
/// <param name="InviteId">The invite, when the code was right.</param>
public sealed record StaffCodeResult(StaffCodeStatus Status, int AttemptsLeft, Guid? InviteId = null);

/// <summary>How joining went.</summary>
public enum StaffJoinStatus
{
    /// <summary>Joined; the organisations are live (or start on their dates).</summary>
    Joined,

    /// <summary>Stopped: the account's name or date of birth differs from what the admin entered. The admin is told.</summary>
    Stopped,

    /// <summary>Not this account, or the link is no longer open.</summary>
    Refused,
}

/// <summary>The result of joining.</summary>
/// <param name="Status">How it went.</param>
/// <param name="Message">What to tell the person.</param>
public sealed record StaffJoinResult(StaffJoinStatus Status, string Message);

/// <summary>An addition still waiting, as an admin sees it.</summary>
/// <param name="Id">The waiting addition.</param>
/// <param name="StaffRef">Its activation, when the person has not activated yet.</param>
/// <param name="Name">The person's name as entered (or their own, once joined).</param>
/// <param name="Roles">Role codes.</param>
/// <param name="StartsOn">Its start date, if later.</param>
/// <param name="Activated">Whether the person has activated (then it waits only for the date).</param>
/// <param name="ExpiresAt">When the activation link stops working.</param>
/// <param name="AddedBeforeDigiLocker">Whether it was added under the temporary exception.</param>
public sealed record StaffWaiting(Guid Id, Guid? StaffRef, string Name, IReadOnlyList<string> Roles, DateOnly? StartsOn, bool Activated, DateTimeOffset? ExpiresAt, bool AddedBeforeDigiLocker);

/// <summary>
/// rc.12 (founder decisions, 14 Oct 2026): adding a person to an organisation safely. Only a DigiLocker-verified admin may add
/// (an operator may switch on a temporary exception per application). A person is linked only by their own act: entering
/// the code the admin gave them (push), or giving the admin a join code they made (pull).
/// </summary>
public interface IStaffService
{
    /// <summary>The check before adding (4.3): at most five per person per admin per day, each audited.</summary>
    /// <param name="actor">The admin.</param>
    /// <param name="orgId">The organisation the person would be added to.</param>
    /// <param name="details">What the admin entered.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The answer, or <see langword="null"/> when the admin may not add there.</returns>
    Task<StaffCheckResult?> CheckAsync(StaffActor actor, Guid orgId, StaffDetails details, CancellationToken cancellationToken = default);

    /// <summary>Adds one person to one organisation.</summary>
    /// <param name="actor">The admin.</param>
    /// <param name="orgId">The organisation.</param>
    /// <param name="addition">Who, and with which roles.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The result.</returns>
    Task<StaffAddResult> AddAsync(StaffActor actor, Guid orgId, StaffAddition addition, CancellationToken cancellationToken = default);

    /// <summary>The additions waiting at an organisation.</summary>
    /// <param name="actor">The admin.</param>
    /// <param name="orgId">The organisation.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The waiting additions, or <see langword="null"/> when the admin may not see them.</returns>
    Task<IReadOnlyList<StaffWaiting>?> WaitingAsync(StaffActor actor, Guid orgId, CancellationToken cancellationToken = default);

    /// <summary>Cancels one waiting addition (and the activation link, when nothing else waits on it).</summary>
    /// <param name="actor">The admin.</param>
    /// <param name="waitingId">The waiting addition.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Whether it was cancelled.</returns>
    Task<bool> CancelAsync(StaffActor actor, Guid waitingId, CancellationToken cancellationToken = default);

    /// <summary>What the person sees on opening the e-mailed link.</summary>
    /// <param name="token">The link's token.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The view, or <see langword="null"/> for an unknown token.</returns>
    Task<StaffInviteView?> GetInviteAsync(string token, CancellationToken cancellationToken = default);

    /// <summary>The activation code the admin gave the person. Five wrong codes end the link.</summary>
    /// <param name="token">The link's token.</param>
    /// <param name="code">The code typed.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The answer.</returns>
    Task<StaffCodeResult> CheckCodeAsync(string token, string code, CancellationToken cancellationToken = default);

    /// <summary>
    /// Joins, for the signed-in person, after the right code. An existing account must be the one the admin's check found,
    /// and its name and date of birth must match what the admin entered unless the admin confirmed the difference; a new
    /// account must have the address the link went to (its own details win).
    /// </summary>
    /// <param name="inviteId">The invite (from a right code).</param>
    /// <param name="userId">The signed-in person.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The result.</returns>
    Task<StaffJoinResult> JoinAsync(Guid inviteId, Guid userId, CancellationToken cancellationToken = default);

    /// <summary>The e-mail address an open invite was sent to (to fill in the registration form), or <see langword="null"/>.</summary>
    /// <param name="inviteId">The invite.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The address and the details the admin entered, or <see langword="null"/>.</returns>
    Task<StaffDetails?> InvitedDetailsAsync(Guid inviteId, CancellationToken cancellationToken = default);
}

/// <summary>A join code the person made (4.1), shown once.</summary>
/// <param name="Code">The code, for example <c>K7Q2-M9XR</c>.</param>
/// <param name="ExpiresAt">When it stops working.</param>
/// <param name="QrSvg">The code as an inline SVG QR code.</param>
public sealed record JoinCodeIssued(string Code, DateTimeOffset ExpiresAt, string QrSvg);

/// <summary>rc.12 (pull): the person's own join codes, made in the portal.</summary>
public interface IJoinCodeService
{
    /// <summary>Makes a new join code (any earlier one stops working).</summary>
    /// <param name="userId">The person.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The code.</returns>
    Task<JoinCodeIssued> CreateAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>Ends the person's live join code, if any.</summary>
    /// <param name="userId">The person.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task.</returns>
    Task CancelAsync(Guid userId, CancellationToken cancellationToken = default);
}
