using System.IO.Compression;

namespace Sangam.Identity.Application.Security;

/// <summary>Verdict shown by the strength meter.</summary>
public enum PasswordVerdict
{
    /// <summary>Below policy; the form will not accept it.</summary>
    Weak = 0,

    /// <summary>Meets policy; could be longer.</summary>
    Fair = 1,

    /// <summary>Meets policy and is <see cref="PasswordStrength.StrongLength"/>+ characters.</summary>
    Strong = 2,
}

/// <summary>Result of the server-side strength estimate: what the meter shows and the requirement rows.</summary>
/// <param name="Verdict">Overall verdict.</param>
/// <param name="Segments">Filled meter segments, 0–4.</param>
/// <param name="HasMinimumLength">At least <paramref name="MinimumLength"/> characters.</param>
/// <param name="HasUppercase">Contains an uppercase letter.</param>
/// <param name="HasLowercase">Contains a lowercase letter.</param>
/// <param name="HasDigit">Contains a digit.</param>
/// <param name="HasSymbol">Contains a non-alphanumeric character.</param>
/// <param name="IsNotBlocklisted">Not on the built-in list of common passwords and not a single repeated character.</param>
/// <param name="IsNotTooLong">At most <see cref="PasswordStrength.MaximumLength"/> characters (R7, ASVS V2.1.2).</param>
/// <param name="MinimumLength">The shortest password the policy in force allows.</param>
/// <param name="RequiresCharacterTypes">Whether the policy in force requires all four character types (an
/// organisation's or application's own choice, rc.5; never the platform default).</param>
public sealed record PasswordStrengthResult(
    PasswordVerdict Verdict,
    int Segments,
    bool HasMinimumLength,
    bool HasUppercase,
    bool HasLowercase,
    bool HasDigit,
    bool HasSymbol,
    bool IsNotBlocklisted,
    bool IsNotTooLong = true,
    int MinimumLength = PasswordStrength.MinimumLength,
    bool RequiresCharacterTypes = false)
{
    /// <summary>Contains all four character classes.</summary>
    public bool HasAllClasses => HasUppercase && HasLowercase && HasDigit && HasSymbol;

    /// <summary>Whether the password may be accepted at all.</summary>
    public bool MeetsPolicy => HasMinimumLength && (HasAllClasses || !RequiresCharacterTypes) && IsNotBlocklisted && IsNotTooLong;

    /// <summary>
    /// The requirement tokens shown beside the verdict, in display order: the length, then — only where the policy
    /// requires them — the four character types. Codes: <c>length</c>, <c>upper</c>, <c>lower</c>, <c>number</c>,
    /// <c>symbol</c>.
    /// </summary>
    public IReadOnlyList<(string Code, bool Met)> Tokens
        => RequiresCharacterTypes
            ? [("length", HasMinimumLength), ("upper", HasUppercase), ("lower", HasLowercase), ("number", HasDigit), ("symbol", HasSymbol)]
            : [("length", HasMinimumLength)];
}

/// <summary>
/// Server-side password policy and strength estimate (rc.5, ASVS V2.1.1, V2.1.7 and V2.1.9): at least
/// <see cref="MinimumLength"/> characters, at most <see cref="MaximumLength"/>, and not one of the most common
/// passwords. No character-type rules unless an organisation or application has chosen to require them. Deterministic
/// and local (no network call) so the no-JavaScript form renders the same meter after a failed post; the online
/// breached-password check runs separately when the password is saved. ASP.NET Core Identity is configured with the
/// same length.
/// </summary>
public static class PasswordStrength
{
    /// <summary>Minimum length accepted (ASVS V2.1.1).</summary>
    public const int MinimumLength = 12;

    /// <summary>Longest password accepted when one is set (R7, ASVS V2.1.2: 64 or more allowed, over 128 refused).
    /// Passwords set before R7 are still checked at sign-in whatever their length.</summary>
    public const int MaximumLength = 128;

    /// <summary>Length at which a policy-compliant password is reported as strong.</summary>
    public const int StrongLength = 16;

    /// <summary>The embedded list of common passwords (see <see cref="CommonPasswordCount"/>).</summary>
    public const string CommonPasswordsResource = "Sangam.Identity.Application.Security.common-passwords.txt.gz";

    private static readonly Lazy<HashSet<string>> Common = new(LoadCommon);

    /// <summary>
    /// How many passwords the built-in list holds: the 10,000 most common passwords of 12 characters or more from
    /// the Pwned Passwords top-million list (SecLists, MIT licence), plus a few Indian patterns. It is the fallback
    /// when the online breached-password service does not answer, and it meets ASVS V2.1.7 on its own.
    /// </summary>
    public static int CommonPasswordCount => Common.Value.Count;

    /// <summary>Estimates the strength of <paramref name="password"/> under the platform's default policy.</summary>
    /// <param name="password">The candidate password; <see langword="null"/> is treated as empty.</param>
    /// <returns>The estimate.</returns>
    public static PasswordStrengthResult Evaluate(string? password) => Evaluate(password, MinimumLength, requireCharacterTypes: false);

    /// <summary>Estimates the strength of <paramref name="password"/> under a policy.</summary>
    /// <param name="password">The candidate password; <see langword="null"/> is treated as empty.</param>
    /// <param name="minimumLength">The shortest password the policy allows (never below <see cref="MinimumLength"/>).</param>
    /// <param name="requireCharacterTypes">Whether the policy requires all four character types.</param>
    /// <returns>The estimate.</returns>
    public static PasswordStrengthResult Evaluate(string? password, int minimumLength, bool requireCharacterTypes)
    {
        string p = password ?? string.Empty;
        int min = Math.Max(minimumLength, MinimumLength);
        bool minimum = p.Length >= min;
        bool upper = p.Any(char.IsUpper);
        bool lower = p.Any(char.IsLower);
        bool digit = p.Any(char.IsDigit);
        bool symbol = p.Any(c => !char.IsLetterOrDigit(c));
        bool notBlocked = p.Length > 0 && !IsCommon(p) && !IsRepetitive(p);
        bool typesOk = !requireCharacterTypes || (upper && lower && digit && symbol);
        bool notTooLong = p.Length <= MaximumLength;

        if (!minimum || !typesOk || !notBlocked || !notTooLong)
        {
            int weakSegments = p.Length == 0 ? 0 : 1;
            return new PasswordStrengthResult(PasswordVerdict.Weak, weakSegments, minimum, upper, lower, digit, symbol, notBlocked, notTooLong, min, requireCharacterTypes);
        }

        int score = 2;
        if (p.Length >= 14)
        {
            score++;
        }

        if (p.Length >= StrongLength)
        {
            score++;
        }

        PasswordVerdict verdict = score >= 4 ? PasswordVerdict.Strong : PasswordVerdict.Fair;
        return new PasswordStrengthResult(verdict, score, minimum, upper, lower, digit, symbol, notBlocked, true, min, requireCharacterTypes);
    }

    /// <summary>Whether <paramref name="password"/> is on the built-in list of common passwords (any letter case).</summary>
    /// <param name="password">The password.</param>
    public static bool IsCommon(string password)
    {
        ArgumentNullException.ThrowIfNull(password);
        return Common.Value.Contains(password);
    }

    private static bool IsRepetitive(string p)
    {
        char first = p[0];
        return p.All(c => c == first);
    }

    private static HashSet<string> LoadCommon()
    {
        HashSet<string> set = new(StringComparer.OrdinalIgnoreCase)
        {
            // Indian patterns people reach for when asked for a long password (rc.5).
            "Welcome@12345", "Welcome@123456", "India@123456", "India@1234567", "Bharat@123456", "Password@123", "Password@1234",
            "Password@12345", "Sangam@123456", "Sangamid@123", "Hospital@1234", "Hospital@12345", "Doctor@123456", "Nurse@1234567",
            "Admin@1234567", "Admin@12345678", "Mumbai@123456", "Pune@12345678", "Ahmedabad@123", "Bangalore@123", "Chennai@12345",
            "Hyderabad@123", "Kolkata@12345", "Ganesh@123456", "Krishna@12345", "Jaishreeram@1", "Jaihind@12345", "Iloveindia@123",
        };

        using Stream stream = typeof(PasswordStrength).Assembly.GetManifestResourceStream(CommonPasswordsResource)
            ?? throw new InvalidOperationException("The built-in list of common passwords is missing from the build.");
        using GZipStream gzip = new(stream, CompressionMode.Decompress);
        using StreamReader reader = new(gzip);
        while (reader.ReadLine() is string line)
        {
            if (line.Length > 0)
            {
                set.Add(line);
            }
        }

        return set;
    }
}
