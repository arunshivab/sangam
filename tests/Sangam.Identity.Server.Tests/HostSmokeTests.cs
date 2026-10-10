using System.Reflection;

namespace Sangam.Identity.Server.Tests;

/// <summary>Boots the host in-process and checks the foundation page renders the brand.</summary>
[Collection("server")]
public sealed class HostSmokeTests
{
    private readonly SangamServerFactory _factory;

    public HostSmokeTests(SangamServerFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Root_RendersSangamLockup()
    {
        using HttpClient client = _factory.CreateClient();

        using HttpResponseMessage response = await client.GetAsync(new Uri("/", UriKind.Relative));
        string html = await response.Content.ReadAsStringAsync();

        Assert.True(response.IsSuccessStatusCode, $"Expected 2xx, got {(int)response.StatusCode}: {html}");
        Assert.Contains("<span class=\"sg-wordmark\">sangam</span>", html, StringComparison.Ordinal);
        Assert.Contains("One sign-in for the applications you use", html, StringComparison.Ordinal);
        Assert.Contains("/.well-known/security.txt", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Foundation check", html, StringComparison.Ordinal);
        Assert.Contains("_content/Sangam.Web.Shared/css/sangam-tokens.css", html, StringComparison.Ordinal);
        Assert.Contains("_content/Sangam.Web.Shared/fonts/lipi/lipi.css", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Root_ShowsTheFullVersion_ReleaseCandidateIncluded()
    {
        using HttpClient client = _factory.CreateClient();
        string expected = typeof(Pages.IndexModel).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+')[0];

        string html = await client.GetStringAsync(new Uri("/", UriKind.Relative));

        // The release candidate suffix (for example 1.0.0-rc.1) is shown, not cut back to 1.0.0.
        Assert.Contains("Version " + expected + "</p>", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Root_LoadsNothingFromExternalHosts()
    {
        using HttpClient client = _factory.CreateClient();

        string html = await client.GetStringAsync(new Uri("/", UriKind.Relative));

        Assert.DoesNotContain("href=\"http", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("src=\"http", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("fonts.googleapis.com", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("rel=\"preconnect\"", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SecurityTxt_NamesTheContact_AndNeverExpiresWithinTheNextMonths()
    {
        // rc.5 (RFC 9116): how a researcher or CERT-In reports a problem.
        using HttpClient client = _factory.CreateClient();
        using HttpResponseMessage response = await client.GetAsync(new Uri("/.well-known/security.txt", UriKind.Relative));
        string text = await response.Content.ReadAsStringAsync();

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/plain", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("Contact: mailto:security@sangamid.in", text, StringComparison.Ordinal);
        Assert.Contains("Canonical: https://", text, StringComparison.Ordinal);
        string expires = text.Split('\n').Single(l => l.StartsWith("Expires: ", StringComparison.Ordinal))["Expires: ".Length..];
        Assert.True(DateTimeOffset.Parse(expires, System.Globalization.CultureInfo.InvariantCulture) > DateTimeOffset.UtcNow.AddDays(150));
    }
}
