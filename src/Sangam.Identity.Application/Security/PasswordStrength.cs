namespace Sangam.Identity.Application.Security;

/// <summary>Verdict shown by the strength meter.</summary>
public enum PasswordVerdict
{
    /// <summary>Below policy; the form will not accept it.</summary>
    Weak = 0,

    /// <summary>Meets policy; could be longer.</summary>
    Fair = 1,

    /// <summary>Meets policy and is 14+ characters.</summary>
    Strong = 2,
}

/// <summary>Result of the server-side strength estimate: what the meter shows and the requirement rows.</summary>
/// <param name="Verdict">Overall verdict.</param>
/// <param name="Segments">Filled meter segments, 0–4.</param>
/// <param name="HasMinimumLength">At least <see cref="PasswordStrength.MinimumLength"/> characters.</param>
/// <param name="HasUppercase">Contains an uppercase letter.</param>
/// <param name="HasLowercase">Contains a lowercase letter.</param>
/// <param name="HasDigit">Contains a digit.</param>
/// <param name="HasSymbol">Contains a non-alphanumeric character.</param>
/// <param name="IsNotBlocklisted">Not on the local blocklist and not a single repeated character.</param>
public sealed record PasswordStrengthResult(
    PasswordVerdict Verdict,
    int Segments,
    bool HasMinimumLength,
    bool HasUppercase,
    bool HasLowercase,
    bool HasDigit,
    bool HasSymbol,
    bool IsNotBlocklisted)
{
    /// <summary>Contains all four character classes.</summary>
    public bool HasAllClasses => HasUppercase && HasLowercase && HasDigit && HasSymbol;

    /// <summary>Whether the password may be accepted at all.</summary>
    public bool MeetsPolicy => HasMinimumLength && HasAllClasses && IsNotBlocklisted;

    /// <summary>The five requirement tokens shown beside the verdict, in display order.</summary>
    public IReadOnlyList<(string Label, bool Met)> Tokens
    => [
        ($"{PasswordStrength.MinimumLength}+", HasMinimumLength),
        ("upper", HasUppercase),
        ("lower", HasLowercase),
        ("number", HasDigit),
        ("symbol", HasSymbol),
    ];
}

/// <summary>
/// Server-side password policy and strength estimate — the same policy as Anjal: at least
/// <see cref="MinimumLength"/> characters, all four character classes, and not on the blocklist.
/// Deterministic and local (no network call) so the no-JavaScript form renders the same meter
/// after a failed post. ASP.NET Core Identity is configured with the identical rules.
/// </summary>
public static class PasswordStrength
{
    /// <summary>Minimum length accepted.</summary>
    public const int MinimumLength = 8;

    /// <summary>Length at which a policy-compliant password is reported as strong.</summary>
    public const int StrongLength = 14;

    private static readonly HashSet<string> Blocklist = new(StringComparer.OrdinalIgnoreCase)
    {
        "Password1!", "Password1@", "Password123!", "P@ssw0rd", "P@ssword1", "Passw0rd!", "Welcome1!", "Welcome@123",
        "Admin@123", "Admin123!", "Qwerty123!", "Qwerty@123", "Abcd@1234", "Abc@12345", "India@123", "Sangam@123",
        "Letmein1!", "Iloveyou1!", "Changeme1!", "Summer2026!", "Winter2026!", "Hospital@1", "Doctor@123", "Nurse@1234",
    };

    /// <summary>Estimates the strength of <paramref name="password"/>.</summary>
    /// <param name="password">The candidate password; <see langword="null"/> is treated as empty.</param>
    /// <returns>The estimate.</returns>
    public static PasswordStrengthResult Evaluate(string? password)
    {
        string p = password ?? string.Empty;
        bool minimum = p.Length >= MinimumLength;
        bool upper = p.Any(char.IsUpper);
        bool lower = p.Any(char.IsLower);
        bool digit = p.Any(char.IsDigit);
        bool symbol = p.Any(c => !char.IsLetterOrDigit(c));
        bool notBlocked = p.Length > 0 && !Blocklist.Contains(p) && !IsRepetitive(p);
        bool allClasses = upper && lower && digit && symbol;

        if (!minimum || !allClasses || !notBlocked)
        {
            int weakSegments = p.Length == 0 ? 0 : 1;
            return new PasswordStrengthResult(PasswordVerdict.Weak, weakSegments, minimum, upper, lower, digit, symbol, notBlocked);
        }

        int score = 2;
        if (p.Length >= 11)
        {
            score++;
        }

        if (p.Length >= StrongLength)
        {
            score++;
        }

        PasswordVerdict verdict = score >= 4 ? PasswordVerdict.Strong : PasswordVerdict.Fair;
        return new PasswordStrengthResult(verdict, score, minimum, upper, lower, digit, symbol, notBlocked);
    }

    private static bool IsRepetitive(string p)
    {
        char first = p[0];
        return p.All(c => c == first);
    }
}
