using System.Globalization;
using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Sangam.Web.Shared.Localization;

namespace Sangam.Web.Shared.Hosting;

/// <summary>
/// rc.16 (founder decision of 26 Oct 2026): the pages a visitor sees when an address does not exist (404) or a request
/// fails (500), on all four hosts. Each is a short, self-contained page written straight to the response, in the
/// visitor's language, with the Sangam mark and colours inline: it needs no layout, no sign-in and no database, so it
/// still shows when the thing that failed is Sangam itself. A failure shows only a reference to quote (the request's
/// id, which every log line of that request carries as <c>RequestId</c>), never the error.
/// </summary>
public static class StatusPages
{
    /// <summary>Where help is asked for when <c>Sangam:SupportEmail</c> is not set.</summary>
    public const string DefaultSupportEmail = "help@sangamid.in";

    private const string Mark =
        "<svg width=\"40\" height=\"40\" viewBox=\"0 0 64 64\" aria-hidden=\"true\"><rect x=\"3.25\" y=\"16.25\" width=\"57.5\" height=\"31.5\" rx=\"15.75\" fill=\"none\" stroke=\"#0F3B38\" stroke-width=\"5.5\"/>"
        + "<g stroke=\"#0F3B38\" stroke-width=\"4\" stroke-linecap=\"round\"><path d=\"M33 26.5 H51\"/><path d=\"M33 37.5 H51\"/></g><circle cx=\"20\" cy=\"32\" r=\"6.25\" fill=\"#8A6A2F\"/></svg>";

    private const string Style =
        "body{margin:0;background:#F7F5F0;color:#14202E;font-family:'LiPi Sans',system-ui,sans-serif}"
        + "header{border-bottom:1px solid #E7E2D6}header div{max-width:1160px;margin:0 auto;padding:16px 24px}"
        + ".brand{display:inline-flex;align-items:center;gap:10px;text-decoration:none;color:#0F3B38;font-size:22px;font-weight:650}.brand span{color:#8A6A2F}"
        + "main{max-width:720px;margin:0 auto;padding:96px 24px}"
        + ".eyebrow{margin:0;font-size:12px;letter-spacing:.12em;text-transform:uppercase;color:#8A6A2F}"
        + "h1{margin:16px 0;font-size:40px;line-height:1.1;font-weight:650}p{font-size:18px;line-height:1.6;color:#3A4552}"
        + ".ref{font-size:15px}.ref code{background:#ECE8DD;padding:2px 6px;border-radius:2px}"
        + ".btn{display:inline-block;margin-top:8px;padding:14px 24px;font-size:16px;font-weight:600;text-decoration:none;background:#0F3B38;color:#F2EFE8;border-radius:2px}"
        + ".btn:focus,a:focus{outline:2px solid #8A6A2F;outline-offset:2px}.help{font-size:15px;color:#6B6A63}a{color:#0F3B38}";

    private static IStringLocalizer L => CatalogueStringLocalizer.Shared;

    /// <summary>
    /// Shows the 404 page for a page address that does not exist. Only a browser asking for a page (a GET or HEAD that
    /// accepts HTML) gets it, and only when nothing has written a body: an API's or protocol endpoint's own answer is
    /// left as it is. Place it after the request and profile culture, so the page is in the visitor's language.
    /// </summary>
    /// <param name="app">The application.</param>
    public static IApplicationBuilder UseSangamNotFoundPage(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.Use(async (context, next) =>
        {
            await next(context).ConfigureAwait(false);
            if (ShowsNotFoundPage(context))
            {
                await WriteAsync(context, StatusCodes.Status404NotFound, reference: null).ConfigureAwait(false);
            }
        });
    }

    /// <summary>Whether a finished request gets the 404 page in place of its empty answer.</summary>
    /// <param name="context">The request.</param>
    public static bool ShowsNotFoundPage(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        HttpResponse response = context.Response;
        if (response.StatusCode != StatusCodes.Status404NotFound || response.HasStarted || response.ContentLength is > 0 || !string.IsNullOrEmpty(response.ContentType))
        {
            return false;
        }

        HttpRequest request = context.Request;
        if (!HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method))
        {
            return false;
        }

        // Machine endpoints answer in their own format; only a browser navigating to a page is shown HTML.
        string path = request.Path.Value ?? string.Empty;
        if (MachinePrefixes.Any(p => path.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        return request.Headers.Accept.Any(a => a is not null && a.Contains("text/html", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Writes one of the two pages: 404 (not found) or 500 (failed, with a reference).</summary>
    /// <param name="context">The request.</param>
    /// <param name="statusCode">404 or 500.</param>
    /// <param name="reference">For a failure, the reference to quote.</param>
    public static async Task WriteAsync(HttpContext context, int statusCode, string? reference)
    {
        ArgumentNullException.ThrowIfNull(context);
        string email = context.RequestServices?.GetService<IConfiguration>()?["Sangam:SupportEmail"] is { Length: > 0 } configured ? configured : DefaultSupportEmail;
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.Headers.CacheControl = "no-store";
        if (HttpMethods.IsHead(context.Request.Method))
        {
            return;
        }

        // The error handler runs outside the culture the localization middleware set further in, so for a failure the
        // request's chosen language is taken from the feature it left on the request. (The 404 page runs inside it.)
        CultureInfo before = CultureInfo.CurrentUICulture;
        if (statusCode >= StatusCodes.Status500InternalServerError
            && context.Features.Get<Microsoft.AspNetCore.Localization.IRequestCultureFeature>()?.RequestCulture.UICulture is CultureInfo chosen)
        {
            CultureInfo.CurrentUICulture = chosen;
        }

        try
        {
            await context.Response.WriteAsync(Render(statusCode, reference, email), context.RequestAborted).ConfigureAwait(false);
        }
        finally
        {
            CultureInfo.CurrentUICulture = before;
        }
    }

    /// <summary>The page's HTML, in the current UI culture.</summary>
    /// <param name="statusCode">404 or 500.</param>
    /// <param name="reference">For a failure, the reference to quote.</param>
    /// <param name="supportEmail">Where to write for help.</param>
    public static string Render(int statusCode, string? reference, string supportEmail)
    {
        ArgumentNullException.ThrowIfNull(supportEmail);
        bool notFound = statusCode == StatusCodes.Status404NotFound;
        string eyebrow = notFound ? L["Error 404"] : L["Error"];
        string title = notFound ? L["This page is not here"] : L["Something went wrong"];
        string body = notFound
            ? L["The address may be mistyped, or the page has moved."]
            : L["Sangam could not complete this request. Nothing about your account has changed. Please try again in a few minutes."];
        string link = "<a href=\"mailto:" + Encode(supportEmail) + "\">" + Encode(supportEmail) + "</a>";
        string help = notFound
            ? Sentence(L["Still lost? Write to {0}."], link)
            : Sentence(L["If it happens again, write to {0} and quote the reference."], link);
        string language = CultureInfo.CurrentUICulture.Name is { Length: > 0 } name ? name : SangamLanguages.English;

        StringBuilder html = new();
        html.Append("<!doctype html><html lang=\"").Append(Encode(language)).Append("\"><head><meta charset=\"utf-8\">")
            .Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\"><meta name=\"robots\" content=\"noindex\">")
            .Append("<title>").Append(Encode(title)).Append(" — SangamID</title>")
            .Append("<link rel=\"icon\" type=\"image/svg+xml\" href=\"/_content/Sangam.Web.Shared/icons/favicon.svg\">")
            .Append("<link rel=\"stylesheet\" href=\"/_content/Sangam.Web.Shared/fonts/lipi/lipi.css\">")
            .Append("<style>").Append(Style).Append("</style></head><body>")
            .Append("<header><div><a class=\"brand\" href=\"/\">").Append(Mark).Append("sangam<span>ID</span></a></div></header>")
            .Append("<main><p class=\"eyebrow\">").Append(Encode(eyebrow)).Append("</p>")
            .Append("<h1>").Append(Encode(title)).Append("</h1>")
            .Append("<p>").Append(Encode(body)).Append("</p>");
        if (!notFound && !string.IsNullOrEmpty(reference))
        {
            html.Append("<p class=\"ref\">").Append(Encode(L["Reference"])).Append(": <code>").Append(Encode(reference)).Append("</code></p>");
        }

        html.Append("<a class=\"btn\" href=\"/\">").Append(Encode(L["Go to the start page"])).Append("</a>")
            .Append("<p class=\"help\">").Append(help).Append("</p></main></body></html>");
        return html.ToString();
    }

    private static readonly string[] MachinePrefixes = ["/api/", "/connect/", "/saml/", "/scim/", "/.well-known/", "/_blazor", "/_framework/", "/_content/", "/health/", "/sms/"];

    private static string Encode(string text) => WebUtility.HtmlEncode(text);

    /// <summary>A translated sentence with one link: the sentence is encoded, the link is inserted as it is.</summary>
    private static string Sentence(string translated, string link)
        => Encode(translated).Replace("{0}", link, StringComparison.Ordinal);
}
