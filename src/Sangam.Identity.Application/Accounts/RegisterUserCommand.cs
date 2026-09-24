using Sangam.Identity.Domain.Enums;

namespace Sangam.Identity.Application.Accounts;

/// <summary>Everything the registration screen collects. All fields are required.</summary>
/// <param name="FirstName">Given name.</param>
/// <param name="LastName">Family name.</param>
/// <param name="Email">Email address; becomes the sign-in identifier.</param>
/// <param name="Mobile">Personal mobile in E.164 form; unique among live users.</param>
/// <param name="DateOfBirth">Date of birth.</param>
/// <param name="Gender">Self-declared gender.</param>
/// <param name="Password">Chosen password (policy: 12+ characters).</param>
/// <param name="TermsVersion">Version of the terms the user accepted ("v1").</param>
/// <param name="IpAddress">Client IP for the audit log.</param>
/// <param name="UserAgent">Client user agent for the audit log.</param>
public sealed record RegisterUserCommand(
    string FirstName,
    string LastName,
    string Email,
    string Mobile,
    DateOnly DateOfBirth,
    Gender Gender,
    string Password,
    string TermsVersion,
    string? IpAddress,
    string? UserAgent);
