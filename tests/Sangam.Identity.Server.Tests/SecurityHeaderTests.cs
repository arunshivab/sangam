using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Sangam.Identity.Server.Tests;

/// <summary>
/// R7 (SGM-503): every answer carries the security headers (the ZAP baseline found none), and no page has inline
/// script the policy would block.
/// </summary>
[Collection("server")]
public sealed partial class SecurityHeaderTests
{
    private readonly SangamServerFactory _factory;

    public SecurityHeaderTests(SangamServerFactory factory)
    {
        _factory = factory;
    }

    [Theory]
    [InlineData("/login")]
    [InlineData("/register")]
    [InlineData("/help")]
    [InlineData("/.well-known/openid-configuration")]
    [InlineData("/does-not-exist")]
    public async Task EveryAnswer_CarriesTheSecurityHeaders(string path)
    {
        using HttpClient client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using HttpResponseMessage response = await client.GetAsync(new Uri(path, UriKind.Relative));

        string policy = Assert.Single(response.Headers.GetValues("Content-Security-Policy"));
        Assert.Contains("frame-ancestors 'none'", policy, StringComparison.Ordinal);
        Assert.Contains("script-src 'self' " + Sangam.Web.Shared.Hosting.SecurityHeaders.FormPostScriptSource + ";", policy, StringComparison.Ordinal);
        Assert.Equal("DENY", Assert.Single(response.Headers.GetValues("X-Frame-Options")));
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
        Assert.Equal("strict-origin-when-cross-origin", Assert.Single(response.Headers.GetValues("Referrer-Policy")));
    }

    [Theory]
    [InlineData("/login")]
    [InlineData("/register")]
    [InlineData("/help")]
    public async Task Pages_AreNeverCached(string path)
    {
        // R7 (ASVS V8.2.1): sign-in pages and anything with personal data are not kept by a browser or a proxy.
        using HttpClient client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using HttpResponseMessage response = await client.GetAsync(new Uri(path, UriKind.Relative));
        Assert.True(response.Headers.CacheControl?.NoStore, path + ": " + response.Headers.CacheControl);
    }

    [Fact]
    public async Task JsonAnswers_AreDownloads_NotPages()
    {
        // R7 (ASVS V14.4.2): an API answer opened in a browser tab is not rendered.
        using HttpClient client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using HttpResponseMessage response = await client.GetAsync(new Uri("/.well-known/openid-configuration", UriKind.Relative));
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition?.DispositionType);
        Assert.True(response.Headers.CacheControl?.NoStore);
    }

    [Theory]
    [InlineData("Production", Microsoft.AspNetCore.Http.CookieSecurePolicy.Always)]
    [InlineData("Staging", Microsoft.AspNetCore.Http.CookieSecurePolicy.Always)]
    [InlineData("Development", Microsoft.AspNetCore.Http.CookieSecurePolicy.SameAsRequest)]
    [InlineData("Testing", Microsoft.AspNetCore.Http.CookieSecurePolicy.SameAsRequest)]
    public void Cookies_AreAlwaysSecure_OutsideDevelopment(string environment, Microsoft.AspNetCore.Http.CookieSecurePolicy expected)
        => Assert.Equal(expected, Sangam.Web.Shared.Hosting.SecurityHeaders.CookiePolicy(environment));

    [Theory]
    [InlineData("/login")]
    [InlineData("/register")]
    [InlineData("/login/code")]
    [InlineData("/help")]
    public async Task NoPage_HasInlineScript_OrAnInlineEventHandler(string path)
    {
        using HttpClient client = _factory.CreateClient();
        string html = await client.GetStringAsync(new Uri(path, UriKind.Relative));

        foreach (Match script in ScriptTag().Matches(html))
        {
            Assert.Contains("src=", script.Value, StringComparison.Ordinal);
        }

        Assert.DoesNotMatch(InlineHandler(), html);
        Assert.DoesNotContain("javascript:", html, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("/login")]
    [InlineData("/register")]
    public async Task PasswordFields_OfferToShowThePassword(string path)
    {
        // R7 (ASVS V2.1.12): js/password-reveal.js adds the button; its labels come from the page, in its language.
        using HttpClient client = _factory.CreateClient();
        string html = await client.GetStringAsync(new Uri(path, UriKind.Relative));
        Assert.Contains("<script src=\"/js/password-reveal.js\" defer></script>", html, StringComparison.Ordinal);
        Assert.Contains("data-reveal-show=\"Show password\"", html, StringComparison.Ordinal);
        Assert.Contains("type=\"password\"", html, StringComparison.Ordinal);
    }

    [GeneratedRegex("<script\\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex ScriptTag();

    [GeneratedRegex("<[a-z][^>]*\\son[a-z]+\\s*=", RegexOptions.IgnoreCase)]
    private static partial Regex InlineHandler();
}
