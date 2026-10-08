using Sangam.Identity.Domain.Enums;
using Sangam.Shared.Constants;

namespace Sangam.Identity.Domain;

/// <summary>
/// How strongly, and how recently, a person authenticated (PR-17, SGM-207 §3): the methods used (RFC 8176
/// <c>amr</c>), the assurance level they reach (<c>acr</c>), and what an application asked for with
/// <c>acr_values</c>.
/// </summary>
public static class AuthenticationAssurance
{
    /// <summary>Single factor: a password, or an e-mailed or texted code.</summary>
    public const string Acr1 = SangamAcr.SingleFactor;

    /// <summary>Two factors: a password with a code, an authenticator or an SMS.</summary>
    public const string Acr2 = SangamAcr.TwoFactor;

    /// <summary>Phishing-resistant: a passkey with user verification.</summary>
    public const string Acr3 = SangamAcr.PhishingResistant;

    /// <summary>Signature-grade: level 2 or 3, performed within the last <see cref="SignatureFreshness"/>, with the meaning shown.</summary>
    public const string AcrSign = SangamAcr.Signature;

    /// <summary>How recent a signature-grade authentication must be.</summary>
    public static readonly TimeSpan SignatureFreshness = TimeSpan.FromMinutes(5);

    /// <summary>The values Sangam advertises in <c>acr_values_supported</c>.</summary>
    public static IReadOnlyList<string> Supported { get; } = [Acr1, Acr2, Acr3, AcrSign];

    /// <summary>
    /// The RFC 8176 methods for a sign-in. A passkey is proof of possession with user verification (<c>pop</c>,
    /// <c>mfa</c>); a password is <c>pwd</c>; an e-mailed or authenticator code is <c>otp</c>; a texted code is
    /// <c>sms</c>; two distinct factors add <c>mfa</c>.
    /// </summary>
    /// <param name="mode">How the sign-in began.</param>
    /// <param name="authenticator">Whether an authenticator step followed.</param>
    /// <param name="codeBySms">Whether the code was texted rather than e-mailed.</param>
    public static IReadOnlyList<string> Methods(SignInMode mode, bool authenticator, bool codeBySms)
    {
        if (mode == SignInMode.Passkey)
        {
            return ["pop", "mfa"];
        }

        List<string> methods = [];
        int factors = 0;
        if (mode is SignInMode.Password or SignInMode.PasswordAndOtp)
        {
            methods.Add("pwd");
            factors++;
        }

        if (mode is SignInMode.PasswordAndOtp or SignInMode.OtpOnly)
        {
            methods.Add(codeBySms ? "sms" : "otp");
            factors++;
        }

        if (authenticator)
        {
            if (!methods.Contains("otp"))
            {
                methods.Add("otp");
            }

            factors++;
        }

        if (factors >= 2)
        {
            methods.Add("mfa");
        }

        return methods;
    }

    /// <summary>The level the methods reach: <see cref="Acr3"/> for a passkey, <see cref="Acr2"/> for two factors, else <see cref="Acr1"/>.</summary>
    /// <param name="methods">RFC 8176 methods.</param>
    public static string AcrFor(IEnumerable<string> methods)
    {
        ArgumentNullException.ThrowIfNull(methods);
        List<string> list = [.. methods];
        return list.Contains("pop") ? Acr3 : list.Contains("mfa") ? Acr2 : Acr1;
    }

    /// <summary>The numeric level of an acr value (sign counts as 2); 0 for a value Sangam does not know.</summary>
    /// <param name="acr">The value.</param>
    public static int Level(string? acr) => acr switch
    {
        Acr1 => 1,
        Acr2 or AcrSign => 2,
        Acr3 => 3,
        _ => 0,
    };

    /// <summary>
    /// What an application's <c>acr_values</c> require. Each listed value is acceptable, so the lowest known level
    /// is the requirement; unknown values are ignored (they are voluntary, OpenID Connect Core §3.1.2.1). A listed
    /// <see cref="AcrSign"/> makes the requirement signature-grade, which also demands freshness.
    /// </summary>
    /// <param name="requested">The requested values.</param>
    public static (int Level, bool Signature) Required(IEnumerable<string>? requested)
    {
        List<string> known = [.. (requested ?? []).Where(v => Level(v) > 0)];
        if (known.Count == 0)
        {
            return (0, false);
        }

        return (known.Min(Level), known.Contains(AcrSign) && known.All(v => v == AcrSign || Level(v) >= 2));
    }
}
