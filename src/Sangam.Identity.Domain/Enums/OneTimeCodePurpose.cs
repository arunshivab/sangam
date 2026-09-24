namespace Sangam.Identity.Domain.Enums;

/// <summary>What a <see cref="Entities.OneTimeCode"/> proves.</summary>
public enum OneTimeCodePurpose
{
    /// <summary>Proves control of the email address at registration.</summary>
    EmailVerification = 0,

    /// <summary>Authorises setting a new password.</summary>
    PasswordReset = 1,

    /// <summary>Completes a sign-in in the <c>password_and_otp</c> or <c>otp_only</c> modes.</summary>
    SignIn = 2,
}
