using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Sangam.SelfService.Web.Tests;

/// <summary>rc.9 (ASVS V11.1.4): the portal limits how many requests one caller makes a minute; static files and health checks are not counted.</summary>
[Collection("portal-db")]
public sealed class RateLimitTests : IClassFixture<PortalFactory>
{
    private readonly PortalFactory _factory;

    public RateLimitTests(PortalFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task OverTheLimit_TheAnswerIs429_WithRetryAfter_ButHealthChecksStillAnswer()
    {
        using WebApplicationFactory<Program> limited = _factory.WithWebHostBuilder(b => b.UseSetting("Sangam:RateLimit:HostRequestsPerMinute", "3"));
        using HttpClient client = limited.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("X-Test-Anonymous", "1");
        for (int i = 0; i < 3; i++)
        {
            using HttpResponseMessage allowed = await client.GetAsync(new Uri("/", UriKind.Relative));
            Assert.NotEqual(HttpStatusCode.TooManyRequests, allowed.StatusCode);
        }

        using HttpResponseMessage refused = await client.GetAsync(new Uri("/", UriKind.Relative));
        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        Assert.Equal("60", Assert.Single(refused.Headers.GetValues("Retry-After")));
        Assert.Contains("Too many requests", await refused.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        using HttpResponseMessage health = await client.GetAsync(new Uri("/health/live", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
    }
}
