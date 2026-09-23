using Microsoft.AspNetCore.Mvc.Testing;

namespace Sangam.SelfService.Web.Tests;

/// <summary>Boots the host in-process and checks the foundation page renders the brand.</summary>
public sealed class HostSmokeTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public HostSmokeTests(WebApplicationFactory<Program> factory)
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
        Assert.Contains("No apps linked yet", html, StringComparison.Ordinal);
        Assert.Contains("_content/Sangam.Web.Shared/css/sangam-tokens.css", html, StringComparison.Ordinal);
        Assert.Contains("_content/Sangam.Web.Shared/fonts/lipi/lipi.css", html, StringComparison.Ordinal);
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
}
