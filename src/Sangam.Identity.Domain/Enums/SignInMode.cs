namespace Sangam.Identity.Domain.Enums;

/// <summary>
/// How a sign-in is performed. A user chooses one as their preference; an app may impose one
/// (<see cref="Entities.App.SignInPolicy"/>), in which case the user's preference is ignored.
/// </summary>
public enum SignInMode
{
    /// <summary>Email and password only. The default for every user.</summary>
    Password = 0,

    /// <summary>Email and password, then a one-time code sent by email (second factor).</summary>
    PasswordAndOtp = 1,

    /// <summary>A one-time code sent by email instead of a password (passwordless).</summary>
    OtpOnly = 2,

    /// <summary>A passkey with user verification (WebAuthn): phishing-resistant and counts as two factors (PR-14, SGM-205). Never a preference; only a session mode.</summary>
    Passkey = 3,
}
