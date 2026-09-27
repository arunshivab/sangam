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
public sealed record AdminUserDetail(
    AdminUserRow Row,
    DateOnly DateOfBirth,
    Gender Gender,
    SignInMode SignInPreference,
    DateTimeOffset? LastSignInAt,
    int ActiveSessions,
    IReadOnlyList<string> LinkedApps,
    IReadOnlyList<string> Organisations,
    string? HoldReason);

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
public sealed record AdminAppRow(Guid Id, string ClientId, string DisplayName, string OwnerCompanyName, AppStatus Status, SignInPolicy SignInPolicy, int Users, int Organisations);

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
