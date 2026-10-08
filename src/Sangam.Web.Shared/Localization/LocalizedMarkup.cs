using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Html;
using Microsoft.Extensions.Localization;

namespace Sangam.Web.Shared.Localization;

/// <summary>
/// Whole translated sentences that hold a link, an emphasis or a name (PR-18). Word order differs between English,
/// Hindi and Malayalam, so a sentence is translated as one piece with <c>{0}</c> slots rather than in fragments.
/// The catalogue text is HTML-encoded; an argument that is <see cref="IHtmlContent"/> or <see cref="MarkupString"/>
/// is inserted as it is, and any other argument is encoded.
/// </summary>
public static partial class LocalizedMarkup
{
    /// <summary>For Razor Pages: <c>@L.Html("New to Sangam? {0}", link)</c>.</summary>
    /// <param name="localizer">The localizer.</param>
    /// <param name="key">The English sentence with placeholders.</param>
    /// <param name="arguments">Values for the placeholders.</param>
    public static IHtmlContent Html(this IStringLocalizer localizer, string key, params object?[] arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        return new HtmlString(Build(localizer, key, arguments));
    }

    /// <summary>For Blazor components: <c>@L.Markup("New to Sangam? {0}", link)</c>.</summary>
    /// <param name="localizer">The localizer.</param>
    /// <param name="key">The English sentence with placeholders.</param>
    /// <param name="arguments">Values for the placeholders.</param>
    public static MarkupString Markup(this IStringLocalizer localizer, string key, params object?[] arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        return new(Build(localizer, key, arguments));
    }

    /// <summary>An element around encoded text, to pass as an argument: <c>LocalizedMarkup.Tag("strong", name)</c>.</summary>
    /// <param name="element">Element name, for example <c>strong</c>.</param>
    /// <param name="text">Its text (encoded).</param>
    /// <param name="cssClass">Optional CSS class.</param>
    public static HtmlString Tag(string element, string? text, string? cssClass = null)
    {
        ArgumentNullException.ThrowIfNull(element);
        string css = cssClass is null ? string.Empty : " class=\"" + WebUtility.HtmlEncode(cssClass) + "\"";
        return new HtmlString("<" + element + css + ">" + WebUtility.HtmlEncode(text ?? string.Empty) + "</" + element + ">");
    }

    /// <summary>A link to pass as an argument: <c>LocalizedMarkup.Link("/forgot", L["Forgot password?"])</c>. Both parts are encoded.</summary>
    /// <param name="href">Where it goes.</param>
    /// <param name="text">Its text.</param>
    /// <param name="cssClass">Optional CSS class.</param>
    public static HtmlString Link(string href, string? text, string? cssClass = null)
    {
        ArgumentNullException.ThrowIfNull(href);
        string css = cssClass is null ? string.Empty : " class=\"" + WebUtility.HtmlEncode(cssClass) + "\"";
        return new HtmlString("<a href=\"" + WebUtility.HtmlEncode(href) + "\"" + css + ">" + WebUtility.HtmlEncode(text ?? string.Empty) + "</a>");
    }

    private static string Build(IStringLocalizer localizer, string key, object?[] arguments)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(arguments);
        string template = localizer[key].Value;
        StringBuilder html = new();
        int last = 0;
        foreach (Match hole in Placeholder().Matches(template))
        {
            html.Append(WebUtility.HtmlEncode(template[last..hole.Index]));
            int index = int.Parse(hole.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            object? value = index < arguments.Length ? arguments[index] : null;
            html.Append(value switch
            {
                IHtmlContent content => Render(content),
                MarkupString markup => markup.Value,
                null => string.Empty,
                _ => WebUtility.HtmlEncode(Convert.ToString(value, System.Globalization.CultureInfo.CurrentCulture)),
            });
            last = hole.Index + hole.Length;
        }

        html.Append(WebUtility.HtmlEncode(template[last..]));
        return html.ToString();
    }

    private static string Render(IHtmlContent content)
    {
        using StringWriter writer = new(System.Globalization.CultureInfo.InvariantCulture);
        content.WriteTo(writer, System.Text.Encodings.Web.HtmlEncoder.Default);
        return writer.ToString();
    }

    [GeneratedRegex(@"\{(\d+)\}")]
    private static partial Regex Placeholder();
}
