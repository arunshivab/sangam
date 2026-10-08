using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Sangam.Web.Shared.Localization;

namespace Sangam.Web.Shared.Hosting;

/// <summary>
/// The answer the Blazor hosts give when a request fails outside Development: a short page written straight to the
/// response, with no page behind the sign-in that could itself fail. They used to re-execute <c>/Error</c>, a page that
/// did not exist and sat behind the sign-in; when Sangam could not be reached (the usual cause), the error handler failed
/// too and the visitor saw a bare 500.
/// </summary>
public static class ErrorPage
{
    private static Microsoft.Extensions.Localization.IStringLocalizer L => CatalogueStringLocalizer.Shared;

    /// <summary>Uses the plain error answer.</summary>
    /// <param name="app">The application.</param>
    public static IApplicationBuilder UseSangamErrorPage(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseExceptionHandler(error => error.Run(async context =>
        {
            string title = L["Something went wrong"];
            string body = L["Sangam could not complete this request. If you were signing in, Sangam may be unreachable for a moment: please try again in a few minutes."];
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            context.Response.ContentType = "text/html; charset=utf-8";
            await context.Response.WriteAsync(
                "<!doctype html><html><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\"><title>"
                + WebUtility.HtmlEncode(title) + "</title></head><body style=\"font-family:system-ui,sans-serif;max-width:40rem;margin:4rem auto;padding:0 1rem;color:#1f3b3a\"><h1>"
                + WebUtility.HtmlEncode(title) + "</h1><p>" + WebUtility.HtmlEncode(body) + "</p></body></html>").ConfigureAwait(false);
        }));
    }
}
