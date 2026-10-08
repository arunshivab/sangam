using Microsoft.AspNetCore.Mvc.Testing;

namespace Imagiqa.Web.Tests;

/// <summary>R7 (SGM-503): the demo sends the security headers on every answer, signed in or not.</summary>
public sealed class SecurityHeaderTests : IClassFixture<ImagiqaFactory>
{
    private readonly ImagiqaFactory _factory;

    public SecurityHeaderTests(ImagiqaFactory factory)
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
        await ImagiqaFactory.MigrateAsync();
        using HttpClient client = _factory.ClientFor(People.Person("Asha", People.At(Guid.NewGuid(), "doctor")));
        using HttpResponseMessage response = await client.GetAsync(new Uri("/", UriKind.Relative));
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        string policy = Assert.Single(response.Headers.GetValues("Content-Security-Policy"));
        Assert.StartsWith("default-src 'self'; script-src 'self';", policy, StringComparison.Ordinal);
    }
}
