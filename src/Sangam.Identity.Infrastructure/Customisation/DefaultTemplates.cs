using System.Collections.Frozen;
using System.Text.Json;

namespace Sangam.Identity.Infrastructure.Customisation;

/// <summary>
/// Sangam's built-in e-mail texts (PR-19) in English, Hindi and Malayalam: <c>DefaultTemplates.json</c>, embedded.
/// The English texts are the e-mails Sangam sent before templates existed, word for word.
/// </summary>
public static class DefaultTemplates
{
    private static readonly Lazy<FrozenDictionary<string, FrozenDictionary<string, (string Subject, string Body)>>> All = new(Load);

    /// <summary>The built-in subject and body of a kind in a language, or null.</summary>
    /// <param name="kind">Kind of message.</param>
    /// <param name="language">Culture.</param>
    public static (string Subject, string Body)? Find(string kind, string language)
        => All.Value.TryGetValue(kind, out FrozenDictionary<string, (string Subject, string Body)>? byLanguage) && byLanguage.TryGetValue(language, out (string Subject, string Body) t) ? t : null;

    private static FrozenDictionary<string, FrozenDictionary<string, (string Subject, string Body)>> Load()
    {
        using Stream stream = typeof(DefaultTemplates).Assembly.GetManifestResourceStream("Sangam.Identity.Infrastructure.Customisation.DefaultTemplates.json")
            ?? throw new InvalidOperationException("DefaultTemplates.json is not embedded.");
        Dictionary<string, Dictionary<string, Entry>> raw = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, Entry>>>(stream) ?? [];
        return raw.ToFrozenDictionary(
            k => k.Key,
            k => k.Value.ToFrozenDictionary(l => l.Key, l => (l.Value.subject, l.Value.body), StringComparer.Ordinal),
            StringComparer.Ordinal);
    }

#pragma warning disable IDE1006, SA1300 // JSON property names
    private sealed record Entry(string subject, string body);
#pragma warning restore IDE1006, SA1300
}
