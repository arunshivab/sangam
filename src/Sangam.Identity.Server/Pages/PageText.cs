using Microsoft.Extensions.Localization;
using Sangam.Web.Shared.Localization;

namespace Sangam.Identity.Server.Pages;

/// <summary>The shared text catalogue for page models and endpoints (PR-18).</summary>
internal static class PageText
{
    /// <summary>The catalogue for this request's language.</summary>
    /// <param name="context">The request.</param>
    public static IStringLocalizer For(HttpContext context) => context.RequestServices.GetRequiredService<IStringLocalizer<SangamText>>();
}
