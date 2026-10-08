using Sangam.Identity.Domain.Enums;

namespace Sangam.Identity.Domain;

/// <summary>
/// The security policy that applies to a person in an application (PR-16, SGM-209 §7): sign-in rule, password
/// rules and second factor. Levels combine platform → application → organisation (and its ancestors), and each
/// level can only make it stricter.
/// </summary>
/// <param name="SignIn">The sign-in rule.</param>
/// <param name="MinPasswordLength">Shortest password allowed.</param>
/// <param name="Mfa">Second-factor rule.</param>
/// <param name="BreachedPasswordCheck">Whether passwords must not appear in known breaches.</param>
public sealed record SecurityPolicy(SignInPolicy SignIn, int MinPasswordLength, MfaRequirement Mfa, bool BreachedPasswordCheck)
{
    /// <summary>The longest minimum a policy may set.</summary>
    public const int MaxMinPasswordLength = 64;

    /// <summary>
    /// How strict a sign-in rule is. Password-only and code-only are weakenings only the platform operators may set; each person's
    /// own choice comes next; then always two-step; then passkey only.
    /// </summary>
    /// <param name="policy">The rule.</param>
    public static int Rank(SignInPolicy policy) => policy switch
    {
        SignInPolicy.Password or SignInPolicy.OtpOnly => 0,
        SignInPolicy.Default => 1,
        SignInPolicy.PasswordAndOtp => 2,
        SignInPolicy.PasskeyOnly => 3,
        _ => 1,
    };

    /// <summary>Applies a stricter level on top of this one. A setting that would weaken this one is ignored.</summary>
    /// <param name="signIn">The level's sign-in rule, or <see langword="null"/> to inherit.</param>
    /// <param name="minPasswordLength">The level's minimum length, or <see langword="null"/>.</param>
    /// <param name="mfa">The level's second-factor rule, or <see langword="null"/>.</param>
    /// <param name="breachedPasswordCheck">The level's breach check, or <see langword="null"/>.</param>
    public SecurityPolicy Tighten(SignInPolicy? signIn, int? minPasswordLength, MfaRequirement? mfa, bool? breachedPasswordCheck) => new(
        signIn is SignInPolicy s && Rank(s) > Rank(SignIn) ? s : SignIn,
        Math.Max(MinPasswordLength, Math.Min(minPasswordLength ?? 0, MaxMinPasswordLength)),
        mfa is MfaRequirement m && m > Mfa ? m : Mfa,
        BreachedPasswordCheck || breachedPasswordCheck == true);

    /// <summary>Combines two policies that both apply (for example, two organisations): the stricter of each setting.</summary>
    /// <param name="other">The other policy.</param>
    public SecurityPolicy Strictest(SecurityPolicy other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return Tighten(other.SignIn, other.MinPasswordLength, other.Mfa, other.BreachedPasswordCheck);
    }

    /// <summary>
    /// Checks settings proposed for a lower level against this inherited policy. Returns why they would weaken it,
    /// or <see langword="null"/> when every setting is at least as strict.
    /// </summary>
    /// <param name="signIn">Proposed sign-in rule, or <see langword="null"/> to inherit.</param>
    /// <param name="minPasswordLength">Proposed minimum length, or <see langword="null"/>.</param>
    /// <param name="mfa">Proposed second-factor rule, or <see langword="null"/>.</param>
    /// <param name="breachedPasswordCheck">Proposed breach check, or <see langword="null"/>.</param>
    public string? WhyWeaker(SignInPolicy? signIn, int? minPasswordLength, MfaRequirement? mfa, bool? breachedPasswordCheck)
    {
        if (signIn is SignInPolicy s && Rank(s) < Rank(SignIn))
        {
            return "That sign-in rule is weaker than the one already in force here.";
        }

        if (minPasswordLength is int min && (min < MinPasswordLength || min > MaxMinPasswordLength))
        {
            return $"The shortest password must be between {MinPasswordLength} and {MaxMinPasswordLength} characters.";
        }

        if (mfa is MfaRequirement m && m < Mfa)
        {
            return "That second-factor rule is weaker than the one already in force here.";
        }

        return breachedPasswordCheck == false && BreachedPasswordCheck
            ? "The breached-password check is already required here and cannot be switched off."
            : null;
    }

    /// <summary>Whether a person with this policy must use a second factor.</summary>
    /// <param name="isAdministrator">Whether they administer the application.</param>
    public bool RequiresSecondFactor(bool isAdministrator)
        => Mfa == MfaRequirement.Required || (Mfa == MfaRequirement.RequiredForAdministrators && isAdministrator);
}
