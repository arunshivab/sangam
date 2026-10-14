using Sangam.Identity.Domain.Enums;

namespace Sangam.Identity.Application.Admin;

/// <summary>A user as the console lists them. Deliberately thin: a list is not a dossier.</summary>
/// <param name="Id">User id.</param>
/// <param name="DisplayName">Name.</param>
/// <param name="Email">Email address.</param>
/// <param name="Mobile">Mobile number.</param>
/// <param name="Status">Lifecycle state.</param>
/// <param name="EmailVerified">Whether the email is verified.</param>
/// <param name="MfaEnrolled">Whether an authenticator app is enrolled.</param>
/// <param name="IsOperator">Whether the user holds console access.</param>
/// <param name="CreatedAt">When the account was created.</param>
/// <param name="PurgeAfter">When a requested deletion will run.</param>
/// <param name="OnHold">Whether a hold blocks that deletion.</param>
public sealed record AdminUserRow(
    Guid Id,
    string DisplayName,
    string Email,
    string? Mobile,
    UserStatus Status,
    bool EmailVerified,
    bool MfaEnrolled,
    bool IsOperator,
    DateTimeOffset CreatedAt,
    DateTimeOffset? PurgeAfter,
    bool OnHold);

/// <summary>A user's full record, as shown on the console's detail page. Opening it is audited.</summary>
/// <param name="Row">The summary fields.</param>
/// <param name="DateOfBirth">Date of birth.</param>
/// <param name="Gender">Stated gender.</param>
/// <param name="SignInPreference">How the user prefers to sign in.</param>
/// <param name="LastSignInAt">When they last signed in.</param>
/// <param name="ActiveSessions">Live browser sessions.</param>
/// <param name="LinkedApps">Applications with live access, by name.</param>
/// <param name="Organisations">Organisations they hold a role in, by name.</param>
/// <param name="HoldReason">Why a hold was placed, if one was.</param>
/// <param name="VerifiedBy">How the identity was verified (PR-26: <c>digilocker</c>), if it was.</param>
/// <param name="VerifiedAt">When it was verified.</param>
/// <param name="Attributes">The values applications keep about the person (PR-25).</param>
public sealed record AdminUserDetail(
    AdminUserRow Row,
    DateOnly DateOfBirth,
    Gender Gender,
    SignInMode SignInPreference,
    DateTimeOffset? LastSignInAt,
    int ActiveSessions,
    IReadOnlyList<string> LinkedApps,
    IReadOnlyList<string> Organisations,
    string? HoldReason,
    string? VerifiedBy = null,
    DateTimeOffset? VerifiedAt = null,
    IReadOnlyList<AdminAttributeValue>? Attributes = null);

/// <summary>A value an application keeps about a person, as the console shows it (PR-25).</summary>
/// <param name="AppName">The application.</param>
/// <param name="Label">The attribute's label.</param>
/// <param name="Key">The attribute's key.</param>
/// <param name="Value">The value.</param>
/// <param name="EditableBy"><c>admin</c> or <c>person</c>.</param>
public sealed record AdminAttributeValue(string AppName, string Label, string Key, string Value, string EditableBy);

/// <summary>An operator of the console.</summary>
/// <param name="UserId">Their user account.</param>
/// <param name="DisplayName">Name.</param>
/// <param name="Email">Email address.</param>
/// <param name="Role">Rank on the console.</param>
/// <param name="MfaEnrolled">Whether their authenticator app is enrolled.</param>
/// <param name="GrantedAt">When access was granted.</param>
/// <param name="GrantedByName">Who granted it.</param>
public sealed record OperatorRow(Guid UserId, string DisplayName, string Email, PlatformRole Role, bool MfaEnrolled, DateTimeOffset GrantedAt, string? GrantedByName);

/// <summary>An application in the registry, as the console lists it.</summary>
/// <param name="Id">App id.</param>
/// <param name="ClientId">OAuth client id.</param>
/// <param name="DisplayName">Name.</param>
/// <param name="OwnerCompanyName">Who operates it.</param>
/// <param name="Status">Lifecycle state.</param>
/// <param name="SignInPolicy">The app's sign-in rule.</param>
/// <param name="Users">Users who have granted it access.</param>
/// <param name="Organisations">Organisations registered through it.</param>
/// <param name="PartnerOwners">How many of the partner's own staff can manage it on the partner console.</param>
/// <param name="IsPlatform">Whether it is one of Sangam's own clients: never disabled here, never given partner owners.</param>
/// <param name="BackChannelLogoutUri">Where Sangam posts a signed logout token when a session ends (PR-20); null for none.</param>
/// <param name="FrontChannelLogoutUri">The page Sangam loads in a hidden frame when a session ends in the browser (PR-20); null for none.</param>
/// <param name="SecretSetAt">rc.10: when the client secret was last set, if Sangam has recorded it.</param>
/// <param name="HasSecret">rc.10: whether the application signs in with a client secret at all (a public client has none);
/// <see langword="null"/> when its registration is not found.</param>
/// <param name="AddWithoutDigiLockerUntil">rc.12: until when admins not yet verified with DigiLocker may add people (the
/// temporary exception), if it is on.</param>
public sealed record AdminAppRow(Guid Id, string ClientId, string DisplayName, string OwnerCompanyName, AppStatus Status, SignInPolicy SignInPolicy, int Users, int Organisations, int PartnerOwners, bool IsPlatform, string? BackChannelLogoutUri = null, string? FrontChannelLogoutUri = null, DateTimeOffset? SecretSetAt = null, bool? HasSecret = null, DateTimeOffset? AddWithoutDigiLockerUntil = null)
{
    /// <summary>rc.10 (founder decision, 12 Oct 2026): a client secret older than this is flagged for a change.</summary>
    public static readonly TimeSpan SecretWarnAge = TimeSpan.FromDays(365);

    /// <summary>Whether the secret is older than <see cref="SecretWarnAge"/> at <paramref name="now"/>.</summary>
    /// <param name="now">The time now.</param>
    /// <returns><see langword="true"/> when it is due a change.</returns>
    public bool SecretIsOld(DateTimeOffset now) => SecretSetAt is DateTimeOffset at && now - at > SecretWarnAge;
}

/// <summary>rc.10 (O2): what the users list is filtered by, and which page of it to show.</summary>
/// <param name="Text">Name, e-mail or mobile; empty for everyone.</param>
/// <param name="Status">Only accounts in this state; <see langword="null"/> for any.</param>
/// <param name="Organisation">Only people holding a live role in an organisation whose name contains this.</param>
/// <param name="Page">The page, from 1.</param>
/// <param name="PageSize">Rows a page.</param>
/// <param name="AppId">rc.11: only people who use this application (a live link to it).</param>
/// <param name="TwoStep">rc.11: only people with (<see langword="true"/>) or without an authenticator app.</param>
public sealed record AdminUserQuery(string? Text, UserStatus? Status, string? Organisation, int Page, int PageSize, Guid? AppId = null, bool? TwoStep = null);

/// <summary>rc.10 (O2): one page of the users list.</summary>
/// <param name="Rows">The people on this page, newest account first.</param>
/// <param name="Total">How many match altogether.</param>
/// <param name="Page">This page, from 1.</param>
/// <param name="PageSize">Rows a page.</param>
public sealed record AdminUserPage(IReadOnlyList<AdminUserRow> Rows, int Total, int Page, int PageSize);

/// <summary>rc.10 (O4): what kind of work a queue item is.</summary>
public enum WorkKind
{
    /// <summary>A DigiLocker recovery whose record did not match the account; an operator decides.</summary>
    Recovery = 0,

    /// <summary>A recovery that matched and is waiting out its cooling-off period; an operator may withdraw it.</summary>
    TwoStepReset = 1,

    /// <summary>An open grievance, with its DPDP deadlines.</summary>
    Grievance = 2,
}

/// <summary>rc.10 (O4): one thing waiting for an operator. It names the account, never the DigiLocker record.</summary>
/// <param name="Kind">What it is.</param>
/// <param name="Id">The request or grievance.</param>
/// <param name="Subject">The account's name, or for a grievance its reference and the complainant's name.</param>
/// <param name="Since">When it arrived.</param>
/// <param name="Due">The clock it runs against: the grievance deadline, or when the reset takes effect.</param>
/// <param name="Privileged">A privileged account (an owner decides; 72-hour wait).</param>
/// <param name="Link">Where it is dealt with.</param>
/// <param name="Overdue">Past its deadline.</param>
public sealed record WorkItem(WorkKind Kind, Guid Id, string Subject, DateTimeOffset Since, DateTimeOffset? Due, bool Privileged, string Link, bool Overdue);

/// <summary>rc.10 (O1): what needs an owner's attention.</summary>
public enum AttentionKind
{
    /// <summary>Grievances overdue or due within two days.</summary>
    GrievancesDue = 0,

    /// <summary>Recoveries waiting for a decision.</summary>
    RecoveriesWaiting = 1,

    /// <summary>An open monitoring alert.</summary>
    Alert = 2,

    /// <summary>A partner application nobody can manage yet (no partner owner).</summary>
    AppWithoutOwner = 3,

    /// <summary>A client secret more than a year old.</summary>
    SecretOld = 4,
}

/// <summary>rc.10 (O1): one line under "Needs attention".</summary>
/// <param name="Kind">What it is.</param>
/// <param name="Subject">The application or alert it is about; empty for a count.</param>
/// <param name="Count">How many, for a count.</param>
/// <param name="Due">The nearest deadline, if any.</param>
/// <param name="Link">Where to deal with it.</param>
public sealed record AttentionItem(AttentionKind Kind, string Subject, int Count, DateTimeOffset? Due, string Link);

/// <summary>rc.10 (O1): one row of the recent audit trail — what operators and Sangam itself did.</summary>
/// <param name="At">When.</param>
/// <param name="Actor">The operator's name, or <c>Sangam</c> for the system.</param>
/// <param name="Action">The audit action code.</param>
/// <param name="TargetType">What kind of thing it was done to.</param>
/// <param name="Failed">Whether it was a refusal or a failure.</param>
public sealed record TrailRow(DateTimeOffset At, string Actor, string Action, string? TargetType, bool Failed);

/// <summary>rc.10 (O1): the owner's platform overview.</summary>
/// <param name="People">Accounts that are not deleted.</param>
/// <param name="NewThisWeek">Accounts made in the last seven days.</param>
/// <param name="SignInsToday">Sessions begun in the last 24 hours.</param>
/// <param name="Applications">Active applications.</param>
/// <param name="Attention">What needs attention, soonest deadline first.</param>
/// <param name="Trail">The latest operator and system actions, newest first.</param>
public sealed record AdminOverview(int People, int NewThisWeek, int SignInsToday, int Applications, IReadOnlyList<AttentionItem> Attention, IReadOnlyList<TrailRow> Trail);

/// <summary>Outcome of a console action.</summary>
/// <param name="Succeeded">Whether it was applied.</param>
/// <param name="Message">What to tell the operator.</param>
public sealed record AdminResult(bool Succeeded, string? Message)
{
    /// <summary>A successful result.</summary>
    /// <param name="message">What to tell the operator.</param>
    public static AdminResult Ok(string message) => new(true, message);

    /// <summary>A refused result.</summary>
    /// <param name="message">Why.</param>
    public static AdminResult Refused(string message) => new(false, message);
}

/// <summary>rc.11: what the operator's audit trail is filtered by.</summary>
/// <param name="Action">Actions starting with this, for example <c>admin.</c> or <c>user.login</c>; empty for all.</param>
/// <param name="Actor">Only events by this kind of actor; <see langword="null"/> for any.</param>
/// <param name="Person">Only events by or about the person with this e-mail address.</param>
/// <param name="From">From this day (India time).</param>
/// <param name="To">To this day, inclusive (India time).</param>
/// <param name="OnlyFailed">Only refusals and failures.</param>
/// <param name="Page">The page, from 1.</param>
/// <param name="PageSize">Rows a page.</param>
public sealed record AuditTrailQuery(string? Action, AuditActorType? Actor, string? Person, DateOnly? From, DateOnly? To, bool OnlyFailed, int Page, int PageSize);

/// <summary>rc.11: one event in the operator's audit trail.</summary>
/// <param name="Id">The event's number in the hash chain.</param>
/// <param name="At">When.</param>
/// <param name="ActorType">What kind of actor.</param>
/// <param name="Actor">Who: a person's name, an application's name, or empty for Sangam or someone not signed in.</param>
/// <param name="Action">The action code.</param>
/// <param name="About">The person it was about, when it was about a person.</param>
/// <param name="AboutId">That person's id, to open their record.</param>
/// <param name="Address">The network address it came from.</param>
/// <param name="Failed">Whether it records a refusal or a failure.</param>
public sealed record AuditTrailRow(long Id, DateTimeOffset At, AuditActorType ActorType, string Actor, string Action, string? About, Guid? AboutId, string? Address, bool Failed);

/// <summary>rc.11: one page of the operator's audit trail, newest first.</summary>
/// <param name="Rows">The events.</param>
/// <param name="HasMore">Whether there is an older page.</param>
/// <param name="Page">The page shown.</param>
public sealed record AuditTrailPage(IReadOnlyList<AuditTrailRow> Rows, bool HasMore, int Page);
