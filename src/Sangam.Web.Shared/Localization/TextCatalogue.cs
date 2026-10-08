using System.Collections.Frozen;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Sangam.Web.Shared.Localization;

/// <summary>
/// The translations, one JSON file per language embedded in this assembly (<c>Localization/hi-IN.json</c> and so on):
/// a flat object from the English text to its translation. Plain JSON so a translator can review it without tools.
/// </summary>
public static partial class TextCatalogue
{
    private static readonly Lazy<FrozenDictionary<string, FrozenDictionary<string, string>>> Catalogues = new(Load);
    private static readonly Lazy<FrozenDictionary<string, Template[]>> Templates = new(() => Catalogues.Value.ToFrozenDictionary(c => c.Key, c => BuildTemplates(c.Value), StringComparer.OrdinalIgnoreCase));

    /// <summary>The cultures that have a catalogue.</summary>
    public static IReadOnlyCollection<string> Cultures => Catalogues.Value.Keys;

    /// <summary>The translation of <paramref name="english"/> into <paramref name="culture"/>, or null when there is none.</summary>
    /// <param name="culture">Culture name.</param>
    /// <param name="english">The English text (the key).</param>
    public static string? Find(string culture, string english)
        => Catalogues.Value.TryGetValue(culture, out FrozenDictionary<string, string>? entries) && entries.TryGetValue(english, out string? text) ? text : null;

    /// <summary>
    /// Translates a finished English sentence, such as a message a service built with a name already in it: an exact
    /// entry first, otherwise an entry with placeholders whose fixed words match (<c>"{0} can sign users in again."</c>
    /// translates <c>"LiPi HIS can sign users in again."</c>, carrying <c>LiPi HIS</c> across). Null when neither fits.
    /// </summary>
    /// <param name="culture">Culture name.</param>
    /// <param name="english">The English sentence.</param>
    public static string? Translate(string culture, string english) => Translate(culture, english, depth: 0);

    private static string? Translate(string culture, string english, int depth)
    {
        ArgumentNullException.ThrowIfNull(english);
        string? exact = Find(culture, english);
        if (exact is not null || !Templates.Value.TryGetValue(culture, out Template[]? templates))
        {
            return exact;
        }

        if (depth > 1)
        {
            return null;
        }

        foreach (Template template in templates)
        {
            Match match = template.Pattern.Match(english);
            if (match.Success)
            {
                // A captured part that is itself catalogue text ("A Sangam operator") is translated too; a name is not.
                object[] values = [.. Enumerable.Range(0, template.Count).Select(i =>
                {
                    string part = match.Groups["p" + i.ToString(CultureInfo.InvariantCulture)].Value;
                    return (object)(Translate(culture, part, depth + 1) ?? part);
                })];
                return string.Format(CultureInfo.CurrentCulture, template.Translation, values);
            }
        }

        return null;
    }

    /// <summary>Every entry of one culture's catalogue (empty for none).</summary>
    /// <param name="culture">Culture name.</param>
    public static IReadOnlyDictionary<string, string> Entries(string culture)
        => Catalogues.Value.TryGetValue(culture, out FrozenDictionary<string, string>? entries) ? entries : FrozenDictionary<string, string>.Empty;

    private static Template[] BuildTemplates(FrozenDictionary<string, string> entries)
    {
        List<Template> templates = [];
        foreach ((string key, string translation) in entries)
        {
            MatchCollection holes = Placeholder().Matches(key);
            if (holes.Count == 0)
            {
                continue;
            }

            StringBuilder pattern = new("^");
            HashSet<string> seen = [];
            int last = 0;
            foreach (Match hole in holes)
            {
                pattern.Append(Regex.Escape(key[last..hole.Index]));
                string index = hole.Groups[1].Value;
                pattern.Append(seen.Add(index) ? "(?<p" + index + ">.+?)" : "\\k<p" + index + ">");
                last = hole.Index + hole.Length;
            }

            pattern.Append(Regex.Escape(key[last..])).Append('$');
            int count = seen.Select(i => int.Parse(i, CultureInfo.InvariantCulture)).Max() + 1;
            int fixedLength = key.Length - holes.Sum(h => h.Length);
            templates.Add(new Template(new Regex(pattern.ToString(), RegexOptions.Singleline | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50)), translation, count, fixedLength));
        }

        // The most specific (most fixed words) first.
        return [.. templates.OrderByDescending(t => t.FixedLength)];
    }

    [GeneratedRegex(@"\{(\d+)\}")]
    private static partial Regex Placeholder();

    private sealed record Template(Regex Pattern, string Translation, int Count, int FixedLength);

    private static FrozenDictionary<string, FrozenDictionary<string, string>> Load()
    {
        Assembly assembly = typeof(TextCatalogue).Assembly;
        Dictionary<string, FrozenDictionary<string, string>> all = new(StringComparer.OrdinalIgnoreCase);
        const string Prefix = "Sangam.Web.Shared.Localization."; // i18n-ignore
        foreach (string name in assembly.GetManifestResourceNames().Where(n => n.StartsWith(Prefix, StringComparison.Ordinal) && n.EndsWith(".json", StringComparison.Ordinal)))
        {
            string culture = name[Prefix.Length..^".json".Length];
            using Stream stream = assembly.GetManifestResourceStream(name)!;
            Dictionary<string, string> entries = JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? [];
            all[culture] = entries.Where(e => !string.IsNullOrWhiteSpace(e.Value)).ToFrozenDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal);
        }

        return all.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }
}
