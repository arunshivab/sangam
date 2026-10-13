namespace Sangam.Identity.Domain;

/// <summary>What a person typed into the sign-in field (rc.11): an email address, a mobile number or a login ID.</summary>
public enum SignInIdentifierKind
{
    /// <summary>Not any of the three.</summary>
    Invalid = 0,

    /// <summary>An email address (contains <c>@</c>).</summary>
    Email = 1,

    /// <summary>A mobile number: digits, with an optional country code, spaces, dashes or brackets.</summary>
    Mobile = 2,

    /// <summary>A login ID the person chose (see <see cref="LoginIds"/>).</summary>
    LoginId = 3,
}

/// <summary>
/// rc.11 (founder decision, 13 Oct 2026): people sign in with their email, their mobile number or a login ID, whichever
/// they find easiest. This tells the three apart. A login ID always starts with a letter, so it can never be read as a
/// mobile number, and it never contains <c>@</c>, so it can never be read as an email address.
/// </summary>
public static class SignInIdentifiers
{
    /// <summary>The longest value accepted in the sign-in field.</summary>
    public const int MaxLength = 256;

    /// <summary>Which kind of identifier <paramref name="raw"/> is.</summary>
    /// <param name="raw">What the person typed.</param>
    public static SignInIdentifierKind Classify(string? raw)
    {
        string value = (raw ?? string.Empty).Trim();
        if (value.Length == 0 || value.Length > MaxLength)
        {
            return SignInIdentifierKind.Invalid;
        }

        if (value.Contains('@', StringComparison.Ordinal))
        {
            int at = value.IndexOf('@', StringComparison.Ordinal);
            return at > 0 && at < value.Length - 1 && value.IndexOf('@', at + 1) < 0 && !value.Any(char.IsWhiteSpace)
                ? SignInIdentifierKind.Email
                : SignInIdentifierKind.Invalid;
        }

        if (value.All(c => char.IsAsciiDigit(c) || c is '+' or ' ' or '-' or '(' or ')'))
        {
            int digits = value.Count(char.IsAsciiDigit);
            bool plusFirstOnly = value.IndexOf('+', StringComparison.Ordinal) <= 0 && value.Count(c => c == '+') <= 1;
            return digits is >= 10 and <= 15 && plusFirstOnly ? SignInIdentifierKind.Mobile : SignInIdentifierKind.Invalid;
        }

        return LoginIds.Problem(LoginIds.Normalise(value)) is null ? SignInIdentifierKind.LoginId : SignInIdentifierKind.Invalid;
    }
}

/// <summary>
/// rc.11: the rules for a login ID. Four to thirty characters: lower-case letters, digits, dots and underscores, starting
/// with a letter, not ending with a dot or an underscore, with no two dots or underscores together. Upper case is accepted
/// when typed and stored lower case. A few words are kept for Sangam itself so nobody can pose as its staff.
/// </summary>
public static class LoginIds
{
    /// <summary>The shortest login ID.</summary>
    public const int MinLength = 4;

    /// <summary>The longest login ID.</summary>
    public const int MaxLength = 30;

    /// <summary>How long a login ID someone gave up stays out of reach of anyone else, so colleagues used to typing it
    /// are not sent to a stranger's account.</summary>
    public static readonly TimeSpan ReleaseHold = TimeSpan.FromDays(90);

    /// <summary>Why a login ID cannot be used: too short or long.</summary>
    public const string ProblemLength = "length";

    /// <summary>Why a login ID cannot be used: a character other than a–z, 0–9, dot or underscore.</summary>
    public const string ProblemCharacters = "characters";

    /// <summary>Why a login ID cannot be used: it does not start with a letter.</summary>
    public const string ProblemStart = "start";

    /// <summary>Why a login ID cannot be used: it ends with, or doubles, a dot or an underscore.</summary>
    public const string ProblemPunctuation = "punctuation";

    /// <summary>Why a login ID cannot be used: a word kept for Sangam.</summary>
    public const string ProblemReserved = "reserved";

    private static readonly HashSet<string> Reserved = new(StringComparer.Ordinal)
    {
        "admin", "administrator", "anjal", "help", "helpdesk", "imagiqa", "info", "lipi", "noreply", "no_reply", "null",
        "operator", "owner", "postmaster", "root", "sangam", "sangamid", "security", "siaara", "sigma", "staff", "support",
        "system", "test", "user", "webmaster",
    };

    /// <summary>The stored form: trimmed and lower case.</summary>
    /// <param name="raw">What was typed.</param>
    public static string Normalise(string? raw) => (raw ?? string.Empty).Trim().ToLowerInvariant();

    /// <summary>Why <paramref name="normalised"/> cannot be a login ID, or <see langword="null"/> when it can.</summary>
    /// <param name="normalised">A value from <see cref="Normalise"/>.</param>
    public static string? Problem(string normalised)
    {
        ArgumentNullException.ThrowIfNull(normalised);
        if (normalised.Length is < MinLength or > MaxLength)
        {
            return ProblemLength;
        }

        if (!normalised.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '.' or '_'))
        {
            return ProblemCharacters;
        }

        if (!char.IsAsciiLetterLower(normalised[0]))
        {
            return ProblemStart;
        }

        char last = normalised[^1];
        if (last is '.' or '_' || normalised.Contains("..", StringComparison.Ordinal) || normalised.Contains("__", StringComparison.Ordinal)
            || normalised.Contains("._", StringComparison.Ordinal) || normalised.Contains("_.", StringComparison.Ordinal))
        {
            return ProblemPunctuation;
        }

        return Reserved.Contains(normalised) || Reserved.Contains(normalised.Replace(".", string.Empty, StringComparison.Ordinal).Replace("_", string.Empty, StringComparison.Ordinal))
            ? ProblemReserved
            : null;
    }

    /// <summary>
    /// The suggestion offered first: <c>first.last</c> from the letters of the name, or the part of the email address before
    /// the <c>@</c> when the name has no Latin letters. Empty when neither gives a usable login ID.
    /// </summary>
    /// <param name="firstName">First name.</param>
    /// <param name="lastName">Last name.</param>
    /// <param name="email">Email address.</param>
    public static string Suggest(string? firstName, string? lastName, string? email)
    {
        string first = Letters(firstName);
        string last = Letters(lastName);
        string name = first.Length > 0 && last.Length > 0 ? first + "." + last : first + last;
        string candidate = Fit(name);
        if (Problem(candidate) is null)
        {
            return candidate;
        }

        string local = (email ?? string.Empty).Split('@')[0].ToLowerInvariant();
        string cleaned = new([.. local.Select(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) ? c : '.')]);
        while (cleaned.Contains("..", StringComparison.Ordinal))
        {
            cleaned = cleaned.Replace("..", ".", StringComparison.Ordinal);
        }

        cleaned = Fit(cleaned.TrimStart('.', '0', '1', '2', '3', '4', '5', '6', '7', '8', '9'));
        return Problem(cleaned) is null ? cleaned : string.Empty;
    }

    /// <summary>A variant of <paramref name="suggestion"/> with a number, for when the suggestion is taken.</summary>
    /// <param name="suggestion">The first suggestion.</param>
    /// <param name="number">2, 3, …</param>
    public static string Numbered(string suggestion, int number)
    {
        ArgumentNullException.ThrowIfNull(suggestion);
        string suffix = number.ToString(System.Globalization.CultureInfo.InvariantCulture);
        string stem = suggestion.Length + suffix.Length > MaxLength ? suggestion[..(MaxLength - suffix.Length)].TrimEnd('.', '_') : suggestion;
        return stem + suffix;
    }

    private static string Letters(string? value)
        => new([.. (value ?? string.Empty).ToLowerInvariant().Normalize(System.Text.NormalizationForm.FormD).Where(char.IsAsciiLetterLower)]);

    private static string Fit(string value) => (value.Length > MaxLength ? value[..MaxLength] : value).Trim('.', '_');
}
