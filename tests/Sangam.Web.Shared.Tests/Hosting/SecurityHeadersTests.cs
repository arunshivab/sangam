using Sangam.Web.Shared.Hosting;

namespace Sangam.Web.Shared.Tests.Hosting;

/// <summary>R7 (SGM-503): the Content-Security-Policy every host sends, found missing by the ZAP baseline scan.</summary>
public sealed class SecurityHeadersTests
{
    private static readonly string[] NoOrigins = [];
    private static readonly string?[] MixedOrigins = ["https://id.sangamid.in/", "https://id.sangamid.in/connect", null, "", "not a url", "ftp://x.example"];

    [Fact]
    public void ThePolicy_AllowsScriptOnlyFromSangam_AndNeverInline()
    {
        string policy = SecurityHeaders.Policy(development: false, identityServer: false, NoOrigins);
        Assert.Contains("script-src 'self';", policy, StringComparison.Ordinal);
        Assert.DoesNotContain("unsafe-eval", policy, StringComparison.Ordinal);
        Assert.Equal(1, policy.Split("'unsafe-inline'").Length - 1);
        Assert.Contains("style-src 'self' 'unsafe-inline';", policy, StringComparison.Ordinal);
        Assert.Contains("object-src 'none'", policy, StringComparison.Ordinal);
        Assert.Contains("base-uri 'self';", policy, StringComparison.Ordinal);
    }

    [Fact]
    public void NoSangamPage_MayBeFramed_AndOnlyTheIdentityServerFramesApplications()
    {
        string console = SecurityHeaders.Policy(development: false, identityServer: false, NoOrigins);
        string identity = SecurityHeaders.Policy(development: false, identityServer: true, NoOrigins);
        Assert.Contains("frame-ancestors 'none';", console, StringComparison.Ordinal);
        Assert.Contains("frame-ancestors 'none';", identity, StringComparison.Ordinal);
        Assert.Contains("frame-src 'none';", console, StringComparison.Ordinal);
        Assert.Contains("frame-src 'self' https:;", identity, StringComparison.Ordinal);
        Assert.DoesNotContain("sha256", console, StringComparison.Ordinal);
        Assert.Equal("DENY", SecurityHeaders.Fixed["X-Frame-Options"]);
        Assert.Equal("nosniff", SecurityHeaders.Fixed["X-Content-Type-Options"]);
    }

    [Fact]
    public void Localhost_IsAllowedOnlyInDevelopment()
    {
        Assert.DoesNotContain("localhost", SecurityHeaders.Policy(development: false, identityServer: true, NoOrigins), StringComparison.Ordinal);
        Assert.Contains("form-action 'self' https: http://localhost:*;", SecurityHeaders.Policy(development: true, identityServer: true, NoOrigins), StringComparison.Ordinal);
    }

    [Fact]
    public void OnlyTheIdentityServer_MaySubmitFormsToAnyHttpsSite()
    {
        // R7 (ZAP round 2): applications are answered by form_post from the identity server; nothing else needs it.
        Assert.Contains("form-action 'self' https:;", SecurityHeaders.Policy(development: false, identityServer: true, NoOrigins), StringComparison.Ordinal);
        Assert.Contains("form-action 'self';", SecurityHeaders.Policy(development: false, identityServer: false, NoOrigins), StringComparison.Ordinal);
    }

    [Fact]
    public void OtherOrigins_AreReducedToTheirOrigin_Once_AndBadOnesIgnored()
    {
        string policy = SecurityHeaders.Policy(development: false, identityServer: false, MixedOrigins);
        Assert.Contains("img-src 'self' data: https://id.sangamid.in;", policy, StringComparison.Ordinal);
        Assert.Contains("form-action 'self' https://id.sangamid.in;", policy, StringComparison.Ordinal);
        Assert.DoesNotContain("ftp:", policy, StringComparison.Ordinal);
        Assert.DoesNotContain("/connect", policy, StringComparison.Ordinal);
    }

    [Fact]
    public void TheIdentityServer_AllowsOnlyOpenIddictsFormPostScript_ByItsHash()
    {
        // The hash of "document.form.submit();", as measured on OpenIddict's form_post page in a browser (R7).
        Assert.Equal("'sha256-j7OoGArf6XW6YY4cAyS3riSSvrJRqpSi1fOF9vQ5SrI='", SecurityHeaders.FormPostScriptSource);
        string identity = SecurityHeaders.Policy(development: false, identityServer: true, NoOrigins);
        Assert.Contains("script-src 'self' 'sha256-j7OoGArf6XW6YY4cAyS3riSSvrJRqpSi1fOF9vQ5SrI=';", identity, StringComparison.Ordinal);
    }
}
