using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Sangam.Web.Shared.Hosting;

/// <summary>
/// R7 (SGM-503, ASVS V14.4): the security headers every Sangam host sends. Found missing by the OWASP ZAP baseline scan:
/// no page carried a Content-Security-Policy, framing protection or <c>nosniff</c>, so a sign-in or consent page could
/// be framed by another site (clickjacking).
/// </summary>
/// <remarks>
/// <para>Scripts come only from Sangam's own origin: every page loads its script from a file, and no page has inline
/// script or an inline event handler. Styles allow inline style attributes, which the Blazor reconnection display and a
/// few pages use; a style cannot run code.</para>
/// <para>Forms may lead to any https address (and to localhost in Development and Testing): signing in ends with a
/// redirect to the application's registered address, and the SAML POST binding posts to the service provider.</para>
/// <para>A header the endpoint has already set is left alone (the uploaded-logo endpoint sends its own, stricter
/// policy), except X-Frame-Options, which is always DENY.</para>
/// </remarks>
public static class SecurityHeaders
{
    /// <summary>Header name for the policy.</summary>
    public const string ContentSecurityPolicy = "Content-Security-Policy";

    /// <summary>
    /// R7 (ASVS V8.2.3): sent with every sign-out, so the browser drops cached pages and site storage and the back
    /// button does not show a signed-in page again. Cookies are left to the sign-out itself.
    /// </summary>
    public const string ClearSiteData = "\"cache\", \"storage\"";

    /// <summary>
    /// The one inline script the identity server sends: OpenIddict's <c>response_mode=form_post</c> answer submits its
    /// form with it. ASP.NET Core's OpenID Connect handler asks for form_post by default, so without it every .NET
    /// application (the portal, the consoles, imagiQa) would stop on a blank page after signing in. It is allowed by its
    /// hash, so no other inline script runs.
    /// </summary>
    public const string FormPostScript = "document.form.submit();";

    /// <summary>The CSP source for <see cref="FormPostScript"/>.</summary>
    public static readonly string FormPostScriptSource = "'sha256-" + Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(FormPostScript))) + "'";

    /// <summary>The headers other than the policy, with their values.</summary>
    public static readonly IReadOnlyDictionary<string, string> Fixed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["X-Content-Type-Options"] = "nosniff",
        ["X-Frame-Options"] = "DENY",
        ["Referrer-Policy"] = "strict-origin-when-cross-origin",
        ["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=(), usb=(), serial=(), bluetooth=()",
        ["Cross-Origin-Opener-Policy"] = "same-origin",
    };

    /// <summary>
    /// R7 (ASVS V3.4.1): cookies are always marked Secure, except in Development and Testing, which run over plain
    /// http. Behind Caddy every request is https, so this changes nothing in production but no longer depends on it.
    /// </summary>
    /// <param name="environmentName">The host environment name.</param>
    public static Microsoft.AspNetCore.Http.CookieSecurePolicy CookiePolicy(string environmentName)
        => environmentName is "Development" or "Testing" ? Microsoft.AspNetCore.Http.CookieSecurePolicy.SameAsRequest : Microsoft.AspNetCore.Http.CookieSecurePolicy.Always;

    /// <summary>Sends the security headers on every response.</summary>
    /// <param name="app">The application.</param>
    /// <param name="environmentName">The host's environment: Development and Testing also allow http://localhost.</param>
    /// <param name="identityServer">Whether the host is the identity server: it frames applications' pages (for
    /// front-channel logout) and answers with OpenIddict's form_post page (<see cref="FormPostScript"/>).</param>
    /// <param name="otherOrigins">Other origins the host's pages load images from or send forms to (the identity
    /// server's origin, for the portal and consoles).</param>
    public static IApplicationBuilder UseSangamSecurityHeaders(this IApplicationBuilder app, string environmentName, bool identityServer = false, params string?[] otherOrigins)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(environmentName);
        ArgumentNullException.ThrowIfNull(otherOrigins);
        string policy = Policy(environmentName is "Development" or "Testing", identityServer, otherOrigins);
        return app.Use((context, next) =>
        {
            context.Response.OnStarting(() =>
            {
                IHeaderDictionary headers = context.Response.Headers;

                // R7 (ASVS V8.2.1): pages and JSON answers hold personal data; no browser or proxy keeps a copy. Static
                // files and anything that set its own caching are left alone.
                string? type = context.Response.ContentType;
                if (!headers.ContainsKey("Cache-Control") && type is not null
                    && (type.StartsWith("text/html", StringComparison.OrdinalIgnoreCase) || type.StartsWith("application/json", StringComparison.OrdinalIgnoreCase)))
                {
                    headers.CacheControl = "no-store";
                }

                // R7 (ASVS V14.4.2): a JSON answer opened directly in a browser is saved, never rendered as a page.
                // Programs and scripts that call the API ignore the header.
                if (!headers.ContainsKey("Content-Disposition") && type is not null && type.StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
                {
                    headers.ContentDisposition = "attachment; filename=\"response.json\"";
                }

                if (!headers.ContainsKey(ContentSecurityPolicy))
                {
                    headers[ContentSecurityPolicy] = policy;
                }

                foreach (KeyValuePair<string, string> header in Fixed)
                {
                    // Antiforgery sets X-Frame-Options to SAMEORIGIN on pages with a form; no Sangam page is ever framed.
                    if (!headers.ContainsKey(header.Key) || string.Equals(header.Key, "X-Frame-Options", StringComparison.OrdinalIgnoreCase))
                    {
                        headers[header.Key] = header.Value;
                    }
                }

                return Task.CompletedTask;
            });
            return next(context);
        });
    }

    /// <summary>The Content-Security-Policy for a host.</summary>
    /// <param name="development">Whether http://localhost is allowed too (Development and Testing).</param>
    /// <param name="identityServer">Whether the host is the identity server (frames applications' pages, and
    /// sends the form_post script).</param>
    /// <param name="otherOrigins">Other origins for images and forms; empty and malformed entries are ignored.</param>
    public static string Policy(bool development, bool identityServer, IEnumerable<string?> otherOrigins)
    {
        ArgumentNullException.ThrowIfNull(otherOrigins);
        List<string> origins = [];
        foreach (string? value in otherOrigins)
        {
            if (Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp))
            {
                string origin = uri.GetLeftPart(UriPartial.Authority);
                if (!origins.Contains(origin, StringComparer.OrdinalIgnoreCase))
                {
                    origins.Add(origin);
                }
            }
        }

        string local = development ? " http://localhost:*" : string.Empty;
        string extra = origins.Count == 0 ? string.Empty : " " + string.Join(' ', origins);
        string frames = identityServer ? "'self' https:" + local : "'none'";
        return "default-src 'self'; "
            + "script-src 'self'" + (identityServer ? " " + FormPostScriptSource : string.Empty) + "; "
            + "style-src 'self' 'unsafe-inline'; "
            + "img-src 'self' data:" + extra + "; "
            + "font-src 'self'; "
            + "connect-src 'self'; "
            + "frame-src " + frames + "; "
            + "frame-ancestors 'none'; "
            // The identity server's forms answer to any registered application (form_post, SAML POST, logout); a
            // console's or the portal's forms go only to itself and the origins it names (R7, ZAP round 2).
            + "form-action 'self'" + (identityServer ? " https:" : string.Empty) + local + extra + "; "
            + "base-uri 'self'; "
            + "object-src 'none'";
    }
}
