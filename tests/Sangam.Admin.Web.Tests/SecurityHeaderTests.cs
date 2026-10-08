using Microsoft.AspNetCore.Mvc.Testing;

namespace Sangam.Admin.Web.Tests;

/// <summary>R7 (SGM-503): the console sends the security headers on every answer, signed in or not.</summary>
public sealed class SecurityHeaderTests : IClassFixture<ConsoleFactory>
{
    private readonly ConsoleFactory _factory;

    public SecurityHeaderTests(ConsoleFactory factory)
    {
        _factory = factory;
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/health/live")]
    public async Task EveryAnswer_CarriesTheSecurityHeaders(string path)
    {
        using HttpClient client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using HttpResponseMessage response = await client.GetAsync(new Uri(path, UriKind.Relative));

        string policy = Assert.Single(response.Headers.GetValues("Content-Security-Policy"));
        Assert.Contains("frame-ancestors 'none'", policy, StringComparison.Ordinal);
        Assert.Contains("script-src 'self';", policy, StringComparison.Ordinal);
        Assert.Equal("DENY", Assert.Single(response.Headers.GetValues("X-Frame-Options")));
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
    }

    [PostgresFact]
    public async Task ARenderedPage_CarriesTheFullPolicy_NotBlazorsOwn()
    {
        Guid owner = await _factory.SeedAsync(Sangam.Identity.Domain.Enums.PlatformRole.Owner, mfa: true, "Owner");
        using HttpClient client = _factory.ClientFor(owner);
        using HttpResponseMessage response = await client.GetAsync(new Uri("/", UriKind.Relative));
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("_framework/blazor.web.js", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        AssertTheFullPolicy(response);
    }

    private static void AssertTheFullPolicy(HttpResponseMessage response)
    {
        // R7: Blazor used to replace it with its own "frame-ancestors 'self'" on every rendered page.
        string policy = Assert.Single(response.Headers.GetValues("Content-Security-Policy"));
        Assert.StartsWith("default-src 'self'; script-src 'self';", policy, StringComparison.Ordinal);
        Assert.Contains("frame-ancestors 'none'", policy, StringComparison.Ordinal);
        Assert.Equal("DENY", Assert.Single(response.Headers.GetValues("X-Frame-Options")));
    }
}
