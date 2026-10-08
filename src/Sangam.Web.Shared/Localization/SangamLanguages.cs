namespace Sangam.Web.Shared.Localization;

/// <summary>A language a person can pick, by culture name and by its own name for itself.</summary>
/// <param name="Culture">Culture name, for example <c>hi-IN</c>.</param>
/// <param name="NativeName">The language's name in its own script, for example हिन्दी.</param>
/// <param name="EnglishName">The language's English name.</param>
public sealed record SangamLanguage(string Culture, string NativeName, string EnglishName);

/// <summary>The languages Sangam's screens are translated into (PR-18).</summary>
public static class SangamLanguages
{
    /// <summary>English (India): the source language, and the fallback for any missing translation.</summary>
    public const string English = "en-IN";

    /// <summary>The cookie that remembers a person's choice (ASP.NET Core's request-culture cookie).</summary>
    public const string CookieName = ".AspNetCore.Culture";

    /// <summary>The languages offered in the language picker, English first.</summary>
    public static IReadOnlyList<SangamLanguage> Offered { get; } =
    [
        new(English, "English", "English"),
        new("hi-IN", "हिन्दी", "Hindi"),
        new("ml-IN", "മലയാളം", "Malayalam"),
    ];

    /// <summary>Whether a culture name is one of <see cref="Offered"/> (ordinal, case-insensitive).</summary>
    /// <param name="culture">Culture name.</param>
    public static bool IsOffered(string? culture)
        => culture is not null && Offered.Any(l => string.Equals(l.Culture, culture, StringComparison.OrdinalIgnoreCase));
}
