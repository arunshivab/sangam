using Sangam.Identity.Domain.Enums;

namespace Sangam.Identity.Application.Accounts;

/// <summary>What the hosts may know about a user. Deliberately carries no credential columns.</summary>
/// <param name="Id">Stable user id (the OIDC <c>sub</c>).</param>
/// <param name="FirstName">Given name.</param>
/// <param name="LastName">Family name.</param>
/// <param name="Email">Email address.</param>
/// <param name="EmailVerified">Whether the address has been verified.</param>
/// <param name="Mobile">Personal mobile in E.164 form.</param>
/// <param name="MobileVerified">Whether the mobile has been verified.</param>
/// <param name="DateOfBirth">Date of birth.</param>
/// <param name="Gender">Self-declared gender.</param>
/// <param name="Locale">BCP 47 locale.</param>
/// <param name="SignInPreference">The user's own sign-in mode.</param>
/// <param name="SecurityStamp">Rotates on password reset or forced sign-out; a session whose stamp no longer matches is ended.</param>
public sealed record UserSummary(
    Guid Id,
    string FirstName,
    string LastName,
    string Email,
    bool EmailVerified,
    string? Mobile,
    bool MobileVerified,
    DateOnly DateOfBirth,
    Gender Gender,
    string Locale,
    SignInMode SignInPreference,
    string SecurityStamp)
{
    /// <summary>Gets "First Last".</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(LastName) ? FirstName : FirstName + " " + LastName;
}
