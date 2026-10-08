using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Sangam.Identity.Infrastructure;

namespace Sangam.Identity.Server.Tests;

/// <summary>OI-037: health endpoints and forwarded-headers trust.</summary>
[Collection("server")]
public sealed class HostingTests
{
    private readonly SangamServerFactory _factory;

    public HostingTests(SangamServerFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Live_AnswersWithoutSigningIn()
    {
        using HttpClient client = _factory.CreateClient();
        using HttpResponseMessage response = await client.GetAsync(new Uri(WebHosting.LivePath, UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [PostgresFact]
    public async Task Ready_IsHealthy_WhenTheDatabaseAnswers()
    {
        using HttpClient client = _factory.CreateClient();
        using HttpResponseMessage response = await client.GetAsync(new Uri(WebHosting.ReadyPath, UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public void ForwardedHeaders_TrustOnlyConfiguredProxiesAndNetworks()
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [WebHosting.KnownProxiesKey] = "10.0.0.5",
            [WebHosting.KnownNetworksKey] = "172.18.0.0/16, 192.168.10.0/24",
        }).Build();
        ServiceCollection services = new();
        services.AddSangamWebHosting(configuration);
        using ServiceProvider provider = services.BuildServiceProvider();

        ForwardedHeadersOptions options = provider.GetRequiredService<IOptions<ForwardedHeadersOptions>>().Value;

        Assert.Contains(IPAddress.Parse("10.0.0.5"), options.KnownProxies);
        Assert.Contains(System.Net.IPNetwork.Parse("172.18.0.0/16"), options.KnownIPNetworks);
        Assert.Equal(ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto, options.ForwardedHeaders);
    }
}
