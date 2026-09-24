using Sangam.Identity.Domain.Enums;

namespace Sangam.Identity.Application.Accounts;

/// <summary>Outcome of checking sign-in credentials.</summary>
public enum SignInStatus
{
    /// <summary>Credentials accepted; the host may issue the session cookie.</summary>
    Succeeded = 0,

    /// <summary>Credentials accepted but a one-time code is still required (<see cref="SignInMode.PasswordAndOtp"/>).</summary>
    RequiresOtp = 1,

    /// <summary>Unknown email or wrong password. The screen says the same thing for both.</summary>
    InvalidCredentials = 2,

    /// <summary>Too many failures; try again after the lockout window.</summary>
    LockedOut = 3,

    /// <summary>The email address has not been verified yet.</summary>
    EmailNotVerified = 4,

    /// <summary>The account is suspended or deleted.</summary>
    NotAllowed = 5,
}

/// <summary>Result of a credential check.</summary>
/// <param name="Status">Outcome.</param>
/// <param name="User">The user when the credentials were accepted (also for <see cref="SignInStatus.RequiresOtp"/> and <see cref="SignInStatus.EmailNotVerified"/>).</param>
/// <param name="Mode">The effective sign-in mode that was applied.</param>
public sealed record SignInCheck(SignInStatus Status, UserSummary? User, SignInMode Mode);
