using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Primitives;

namespace Sangam.Identity.Infrastructure;

/// <summary>
/// Reads OpenID Connect <c>ui_locales</c> (space-separated, in order of preference) from the request, or from the
/// <c>returnUrl</c> that carries an authorization request through sign-in, so an application can open Sangam in
/// the person's language. Bare language tags map to India: <c>hi</c> → <c>hi-IN</c>.
/// </summary>
public sealed class UiLocalesRequestCultureProvider : RequestCultureProvider
{
    /// <inheritdoc />
    public override Task<ProviderCultureResult?> DetermineProviderCultureResult(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        string? locales = httpContext.Request.Query["ui_locales"];
        if (string.IsNullOrEmpty(locales) && httpContext.Request.Query["returnUrl"] is { Count: > 0 } returnUrl
            && returnUrl[0] is string inner && inner.IndexOf('?', StringComparison.Ordinal) is int q and >= 0)
        {
            locales = QueryHelpers.ParseQuery(inner[q..]).TryGetValue("ui_locales", out StringValues v) ? v.ToString() : null;
        }

        if (string.IsNullOrWhiteSpace(locales))
        {
            return NullProviderCultureResult;
        }

        foreach (string tag in locales.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            string culture = tag.Contains('-', StringComparison.Ordinal) ? tag : tag + "-IN";
            string? match = WebHosting.SupportedCultures.FirstOrDefault(c => string.Equals(c, culture, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return Task.FromResult<ProviderCultureResult?>(new ProviderCultureResult(match));
            }
        }

        return NullProviderCultureResult;
    }
}
