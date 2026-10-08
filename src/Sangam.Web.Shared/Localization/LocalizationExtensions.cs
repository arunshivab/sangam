using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Localization;

namespace Sangam.Web.Shared.Localization;

/// <summary>Registers Sangam's text catalogue and the language switch (PR-18).</summary>
public static class LocalizationExtensions
{
    /// <summary>The path that switches language: <c>/culture?c=hi-IN&amp;returnUrl=/login</c>.</summary>
    public const string SwitchPath = "/culture";

    /// <summary>
    /// Registers <c>IStringLocalizer&lt;T&gt;</c> backed by <see cref="TextCatalogue"/>, for pages, components and
    /// data-annotation messages alike.
    /// </summary>
    /// <param name="services">Services.</param>
    public static IServiceCollection AddSangamLocalization(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddLocalization();
        services.Replace(ServiceDescriptor.Singleton<IStringLocalizerFactory, CatalogueStringLocalizerFactory>());
        return services;
    }

    /// <summary>
    /// Maps <see cref="SwitchPath"/>: remembers the chosen language in the culture cookie for a year and goes back to
    /// <c>returnUrl</c> (local addresses only). An unknown language is ignored.
    /// </summary>
    /// <param name="endpoints">Endpoints.</param>
    public static IEndpointConventionBuilder MapSangamLanguageSwitch(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        return endpoints.MapGet(SwitchPath, (HttpContext context, string? c, string? returnUrl) =>
        {
            if (SangamLanguages.IsOffered(c))
            {
                string culture = SangamLanguages.Offered.First(l => string.Equals(l.Culture, c, StringComparison.OrdinalIgnoreCase)).Culture;
                context.Response.Cookies.Append(
                    CookieRequestCultureProvider.DefaultCookieName,
                    CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(culture)),
                    new CookieOptions
                    {
                        Expires = DateTimeOffset.UtcNow.AddYears(1),
                        IsEssential = true,
                        HttpOnly = true,
                        Secure = context.Request.IsHttps,
                        SameSite = SameSiteMode.Lax,
                        Path = "/",
                    });
            }

            return Results.LocalRedirect(IsLocal(returnUrl) ? returnUrl! : "/");
        }).AllowAnonymous();
    }

    /// <summary>The address of the language switch for <paramref name="culture"/>, returning to <paramref name="returnUrl"/>.</summary>
    /// <param name="culture">Culture name.</param>
    /// <param name="returnUrl">Where to come back to (path and query).</param>
    public static string SwitchUrl(string culture, string? returnUrl)
        => SwitchPath + "?c=" + Uri.EscapeDataString(culture) + "&returnUrl=" + Uri.EscapeDataString(IsLocal(returnUrl) ? returnUrl! : "/");

    private static bool IsLocal(string? url)
        => !string.IsNullOrEmpty(url) && url[0] == '/' && (url.Length == 1 || (url[1] != '/' && url[1] != '\\'));
}
