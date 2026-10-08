using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Sangam.Identity.Server.Tests;

/// <summary>OI-035: the token endpoint, userinfo and the management API are rate limited.</summary>
[Collection("server")]
public sealed class RateLimitTests
{
    private readonly SangamServerFactory _factory;

    public RateLimitTests(SangamServerFactory factory)
    {
        _factory = factory;
    }

    [Theory]
    [InlineData("TokenPerMinute", "POST", "/connect/token")]
    [InlineData("UserInfoPerMinute", "GET", "/connect/userinfo")]
    [InlineData("ApiPerMinute", "GET", "/api/v1/roles")]
    public async Task TheThirdRequestInAMinute_IsRefused_WhenTheLimitIsTwo(string setting, string method, string path)
    {
        WebApplicationFactory<Program> limited = _factory.WithWebHostBuilder(b => b.UseSetting("Sangam:RateLimit:" + setting, "2"));
        using HttpClient client = limited.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        HttpStatusCode last = HttpStatusCode.OK;
        for (int i = 0; i < 3; i++)
        {
            using HttpRequestMessage request = new(new HttpMethod(method), path);
            if (method == "POST")
            {
                request.Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["grant_type"] = "client_credentials", ["client_id"] = "nobody", ["client_secret"] = "wrong" });
            }

            using HttpResponseMessage response = await client.SendAsync(request);
            last = response.StatusCode;
            if (i < 2)
            {
                Assert.NotEqual(HttpStatusCode.TooManyRequests, last);
            }
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, last);
    }
}
