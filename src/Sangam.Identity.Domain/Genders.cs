using Sangam.Identity.Domain.Enums;

namespace Sangam.Identity.Domain;

/// <summary>Wire codes for <see cref="Gender"/>, as used in the OIDC <c>gender</c> claim and the registration form.</summary>
public static class Genders
{
    /// <summary>The code: <c>female</c>, <c>male</c>, <c>other</c>, <c>prefer_not_to_say</c>.</summary>
    /// <param name="gender">The value.</param>
    public static string ToCode(Gender gender) => gender switch
    {
        Gender.Female => "female",
        Gender.Male => "male",
        Gender.Other => "other",
        _ => "prefer_not_to_say",
    };

    /// <summary>Parses a code produced by <see cref="ToCode"/>.</summary>
    /// <param name="code">The code.</param>
    /// <param name="gender">The value when recognised.</param>
    /// <returns>Whether the code was recognised.</returns>
    public static bool TryParse(string? code, out Gender gender)
    {
        switch (code)
        {
            case "female":
                gender = Gender.Female;
                return true;
            case "male":
                gender = Gender.Male;
                return true;
            case "other":
                gender = Gender.Other;
                return true;
            case "prefer_not_to_say":
                gender = Gender.PreferNotToSay;
                return true;
            default:
                gender = Gender.PreferNotToSay;
                return false;
        }
    }
}
