using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sangam.Identity.Server.Logout;

namespace Sangam.Identity.Server.Tests.Oidc;

/// <summary>
/// V-16: back-channel logout calls go through the outbound guard, as SCIM and webhook calls do: a redirect is never
/// followed, and a private-network address is refused when connecting unless Sangam:Outbound:AllowPrivateNetworks.
/// </summary>
[Collection("server")]
public sealed class LogoutClientGuardTests
{
    private readonly SangamServerFactory _factory;

    public LogoutClientGuardTests(SangamServerFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task TheLogoutClient_DoesNotFollowRedirects()
    {
        await using WebApplication receiver = await StartAsync();
        string address = Address(receiver);
        HttpClient client = _factory.Services.GetRequiredService<IHttpClientFactory>().CreateClient(BackChannelLogoutSender.HttpClientName);
        using FormUrlEncodedContent body = new(new Dictionary<string, string> { ["logout_token"] = "x" });
        using HttpResponseMessage response = await client.PostAsync(new Uri(address + "/bc"), body);
        Assert.Equal(HttpStatusCode.TemporaryRedirect, response.StatusCode);
        Assert.Equal(0, Hits(receiver));
    }

    [Fact]
    public async Task TheLogoutClient_RefusesAPrivateAddress_WhenPrivateNetworksAreNotAllowed()
    {
        await using WebApplication receiver = await StartAsync();
        using WebApplicationFactory<Program> production = _factory.WithWebHostBuilder(b => b.UseSetting("Sangam:Outbound:AllowPrivateNetworks", "false"));
        HttpClient client = production.Services.GetRequiredService<IHttpClientFactory>().CreateClient(BackChannelLogoutSender.HttpClientName);
        using FormUrlEncodedContent body = new(new Dictionary<string, string> { ["logout_token"] = "x" });
        await Assert.ThrowsAsync<HttpRequestException>(() => client.PostAsync(new Uri(Address(receiver) + "/bc"), body));
    }

    private static async Task<WebApplication> StartAsync()
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        WebApplication app = builder.Build();
        int hits = 0;
        app.MapPost("/bc", () => Results.Redirect("/elsewhere", permanent: false, preserveMethod: true));
        app.MapPost("/elsewhere", () =>
        {
            Interlocked.Increment(ref hits);
            app.Configuration["hits"] = hits.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return Results.Ok();
        });
        await app.StartAsync();
        return app;
    }

    private static string Address(WebApplication app) => app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First().TrimEnd('/');

    private static int Hits(WebApplication app) => int.TryParse(app.Configuration["hits"], out int n) ? n : 0;
}
