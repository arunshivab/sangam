using System.Globalization;
using Microsoft.Extensions.Localization;

namespace Sangam.Web.Shared.Localization;

/// <summary>Looks text up in <see cref="TextCatalogue"/> for the current UI culture; English when there is no translation.</summary>
public sealed class CatalogueStringLocalizer : IStringLocalizer
{
    /// <summary>
    /// The one instance, for static helpers that cannot have it injected. It holds no state: every lookup reads the
    /// current UI culture, so it is safe to share.
    /// </summary>
    public static IStringLocalizer Shared { get; } = new CatalogueStringLocalizer();

    /// <inheritdoc />
    public LocalizedString this[string name]
    {
        get
        {
            ArgumentNullException.ThrowIfNull(name);
            string? text = TextCatalogue.Translate(CultureInfo.CurrentUICulture.Name, name);
            return new LocalizedString(name, text ?? name, resourceNotFound: text is null);
        }
    }

    /// <inheritdoc />
    public LocalizedString this[string name, params object[] arguments]
    {
        get
        {
            LocalizedString format = this[name];
            return new LocalizedString(name, string.Format(CultureInfo.CurrentCulture, format.Value, arguments), format.ResourceNotFound);
        }
    }

    /// <inheritdoc />
    public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures)
        => TextCatalogue.Entries(CultureInfo.CurrentUICulture.Name).Select(e => new LocalizedString(e.Key, e.Value));
}

/// <summary>Hands every requester the one catalogue: Sangam has a single shared text catalogue, not one per class.</summary>
public sealed class CatalogueStringLocalizerFactory : IStringLocalizerFactory
{
    private readonly CatalogueStringLocalizer _localizer = new();

    /// <inheritdoc />
    public IStringLocalizer Create(Type resourceSource) => _localizer;

    /// <inheritdoc />
    public IStringLocalizer Create(string baseName, string location) => _localizer;
}
