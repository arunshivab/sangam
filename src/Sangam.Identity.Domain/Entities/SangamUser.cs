using Microsoft.AspNetCore.Identity;
using Sangam.Identity.Domain.Enums;

namespace Sangam.Identity.Domain.Entities;

/// <summary>
/// One row per human, ever. Built on ASP.NET Core Identity's user (email, normalised email,
/// password hash, security stamp, lockout, confirmation flags) with Sangam's additional columns.
/// <para>
/// <see cref="IdentityUser{TKey}.PhoneNumber"/> holds the personal mobile in E.164 form and is
/// unique among non-deleted users; <see cref="IdentityUser{TKey}.PhoneNumberConfirmed"/> stays
/// <see langword="false"/> until mobile OTP verification ships. Email is the sign-in identifier.
/// </para>
/// <para>
/// This entity never leaves the Infrastructure layer: every read is projected into a DTO that
/// carries no credential columns.
/// </para>
/// </summary>
public sealed class SangamUser : IdentityUser<Guid>
{
    /// <summary>Given name.</summary>
    public string FirstName { get; set; } = string.Empty;

    /// <summary>Family name.</summary>
    public string LastName { get; set; } = string.Empty;

    /// <summary>Date of birth (calendar date, no time). Carried to apps as the OIDC <c>birthdate</c> claim.</summary>
    public DateOnly DateOfBirth { get; set; }

    /// <summary>Self-declared gender. Carried to apps as the OIDC <c>gender</c> claim.</summary>
    public Gender Gender { get; set; } = Gender.PreferNotToSay;

    /// <summary>The user's chosen sign-in mode; ignored when the app being signed in to imposes a policy.</summary>
    public SignInMode SignInPreference { get; set; } = SignInMode.Password;

    /// <summary>Gets "First Last" for display.</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(LastName) ? FirstName : FirstName + " " + LastName;

    /// <summary>BCP 47 locale for UI and email language. Defaults to <c>en-IN</c>.</summary>
    public string Locale { get; set; } = "en-IN";

    /// <summary>IANA time zone for rendering timestamps. Defaults to <c>Asia/Kolkata</c>.</summary>
    public string TimeZone { get; set; } = "Asia/Kolkata";

    /// <summary>Lifecycle state.</summary>
    public UserStatus Status { get; set; } = UserStatus.Active;

    /// <summary>When the account was created (UTC).</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>When any column last changed (UTC).</summary>
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>When the user requested deletion (UTC); <see langword="null"/> while active.</summary>
    public DateTimeOffset? DeletedAt { get; set; }

    /// <summary>When the password was last set (UTC). Used for password-age policies.</summary>
    public DateTimeOffset LastPasswordChangeAt { get; set; }
}
