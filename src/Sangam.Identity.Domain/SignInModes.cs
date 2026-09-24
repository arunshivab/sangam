using Sangam.Identity.Domain.Enums;

namespace Sangam.Identity.Domain;

/// <summary>Resolves the effective sign-in mode from an app's policy and a user's preference.</summary>
public static class SignInModes
{
    /// <summary>
    /// The app's rule wins when it has one; otherwise the user's preference applies.
    /// A <see langword="null"/> policy (no app in the flow — signing in to the portal itself) is the same as <see cref="SignInPolicy.Default"/>.
    /// </summary>
    /// <param name="appPolicy">The app's policy, or <see langword="null"/> when no app is in the flow.</param>
    /// <param name="userPreference">The user's preference.</param>
    /// <returns>The mode to enforce.</returns>
    public static SignInMode Resolve(SignInPolicy? appPolicy, SignInMode userPreference) => appPolicy switch
    {
        SignInPolicy.Password => SignInMode.Password,
        SignInPolicy.PasswordAndOtp => SignInMode.PasswordAndOtp,
        SignInPolicy.OtpOnly => SignInMode.OtpOnly,
        _ => userPreference,
    };

    /// <summary>The wire/storage code for a mode: <c>password</c>, <c>password_and_otp</c>, <c>otp_only</c>.</summary>
    /// <param name="mode">The mode.</param>
    public static string ToCode(SignInMode mode) => mode switch
    {
        SignInMode.PasswordAndOtp => "password_and_otp",
        SignInMode.OtpOnly => "otp_only",
        _ => "password",
    };

    /// <summary>Parses a code produced by <see cref="ToCode"/>.</summary>
    /// <param name="code">The code.</param>
    /// <param name="mode">The mode when recognised.</param>
    /// <returns>Whether the code was recognised.</returns>
    public static bool TryParse(string? code, out SignInMode mode)
    {
        switch (code)
        {
            case "password":
                mode = SignInMode.Password;
                return true;
            case "password_and_otp":
                mode = SignInMode.PasswordAndOtp;
                return true;
            case "otp_only":
                mode = SignInMode.OtpOnly;
                return true;
            default:
                mode = SignInMode.Password;
                return false;
        }
    }
}
