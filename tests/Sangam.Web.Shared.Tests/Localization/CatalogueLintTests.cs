using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using Sangam.Web.Shared.Localization;

namespace Sangam.Web.Shared.Tests.Localization;

/// <summary>
/// The i18n lint (PR-18): every screen's text goes through the catalogue, every key a screen needs is translated into
/// Hindi and Malayalam, nothing in a catalogue is stale, and placeholders survive translation. Set
/// <c>SANGAM_I18N_DUMP</c> to a path to write every key with its current translations, for translators.
/// </summary>
public sealed partial class CatalogueLintTests
{
    private static readonly string[] Translated = ["hi-IN", "ml-IN"];
    private static readonly JsonSerializerOptions DumpOptions = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    [Fact]
    public void NoVisibleText_BypassesTheCatalogue()
    {
        List<string> problems = TextSources.Untranslated();
        Assert.True(problems.Count == 0, "Text not going through L[...]:\n" + string.Join('\n', problems));
    }

    [Fact]
    public void EveryKeyAScreenNeeds_IsTranslated_IntoEveryLanguage()
    {
        Dictionary<string, string> keys = TextSources.RequiredKeys();
        Dump(keys);
        List<string> missing = [];
        foreach (string culture in Translated)
        {
            IReadOnlyDictionary<string, string> entries = TextCatalogue.Entries(culture);
            missing.AddRange(keys.Where(k => !entries.ContainsKey(k.Key)).Select(k => $"{culture}: \"{k.Key}\" ({k.Value})"));
        }

        Assert.True(missing.Count == 0, $"{missing.Count} missing translations:\n" + string.Join('\n', missing.Take(200)));
    }

    [Fact]
    public void NoTranslation_IsStale()
    {
        HashSet<string> keys = [.. TextSources.RequiredKeys().Keys];
        List<string> stale = [.. Translated.SelectMany(c => TextCatalogue.Entries(c).Keys.Where(k => !keys.Contains(k)).Select(k => $"{c}: \"{k}\""))];
        Assert.True(stale.Count == 0, "Catalogue entries no screen uses:\n" + string.Join('\n', stale));
    }

    [Fact]
    public void Placeholders_SurviveTranslation_AndEveryKeyHasFixedWords()
    {
        List<string> problems = [];
        foreach (string culture in Translated)
        {
            foreach ((string key, string value) in TextCatalogue.Entries(culture))
            {
                string[] want = [.. Placeholder().Matches(key).Select(m => m.Value).Distinct().Order(StringComparer.Ordinal)];
                string[] got = [.. Placeholder().Matches(value).Select(m => m.Value).Distinct().Order(StringComparer.Ordinal)];
                if (!want.SequenceEqual(got))
                {
                    problems.Add($"{culture}: \"{key}\" → \"{value}\"");
                }

                if (Placeholder().Replace(key, string.Empty).Trim().Length < 2)
                {
                    problems.Add($"{culture}: \"{key}\" has no fixed words");
                }
            }
        }

        Assert.True(problems.Count == 0, string.Join('\n', problems));
    }

    [Fact]
    public void HindiAndMalayalam_HaveCatalogues_AndArePickable()
    {
        foreach (string culture in Translated)
        {
            Assert.Contains(culture, TextCatalogue.Cultures, StringComparer.OrdinalIgnoreCase);
            Assert.True(SangamLanguages.IsOffered(culture));
        }
    }

    [Fact]
    public void AMissingTranslation_ShowsTheEnglish_NeverABlank()
    {
        CultureInfo before = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = new CultureInfo("ml-IN");
            Microsoft.Extensions.Localization.LocalizedString text = new CatalogueStringLocalizer()["A sentence no catalogue has, 42."];
            Assert.True(text.ResourceNotFound);
            Assert.Equal("A sentence no catalogue has, 42.", text.Value);
        }
        finally
        {
            CultureInfo.CurrentUICulture = before;
        }
    }

    [Fact]
    public void AServiceSentenceWithANameInIt_IsTranslatedByItsTemplate()
    {
        (string key, string translation) = TextCatalogue.Entries("hi-IN").FirstOrDefault(e => e.Key.StartsWith("{0} ", StringComparison.Ordinal) && !e.Key.Contains("{1}", StringComparison.Ordinal));
        Assert.NotNull(key);
        string sentence = "LiPi HIS" + key[3..];
        Assert.Equal(translation.Replace("{0}", "LiPi HIS", StringComparison.Ordinal), TextCatalogue.Translate("hi-IN", sentence));
        Assert.Null(TextCatalogue.Translate("hi-IN", "Completely unknown words here."));
    }

    private static void Dump(Dictionary<string, string> keys)
    {
        string? path = Environment.GetEnvironmentVariable("SANGAM_I18N_DUMP");
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        var rows = keys.OrderBy(k => k.Value, StringComparer.Ordinal).ThenBy(k => k.Key, StringComparer.Ordinal).Select(k => new
        {
            key = k.Key,
            where = k.Value,
            hi = TextCatalogue.Find("hi-IN", k.Key),
            ml = TextCatalogue.Find("ml-IN", k.Key),
        });
        File.WriteAllText(path, JsonSerializer.Serialize(rows, DumpOptions));
    }

    [GeneratedRegex(@"\{\d+\}")]
    private static partial Regex Placeholder();
}
