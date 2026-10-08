namespace Sangam.Identity.Domain.Enums;

/// <summary>An app's rule for how its users sign in.</summary>
public enum SignInPolicy
{
    /// <summary>No rule: the user's own <see cref="SignInMode"/> preference applies.</summary>
    Default = 0,

    /// <summary>Email and password only, regardless of the user's preference.</summary>
    Password = 1,

    /// <summary>Password then email code, regardless of the user's preference.</summary>
    PasswordAndOtp = 2,

    /// <summary>Email code instead of a password, regardless of the user's preference.</summary>
    OtpOnly = 3,

    /// <summary>A passkey only: no password or code sign-in for this application (PR-16, SGM-209 §7).</summary>
    PasskeyOnly = 4,
}
