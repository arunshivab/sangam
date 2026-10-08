using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Sangam.Client;

namespace Imagiqa.Web.Tests;

/// <summary>D-I: at demo.sangamid.in every page says it is a demo, and Caddy can check the host without signing in.</summary>
[Collection("imagiqa-db")]
public sealed class DemoTests : IClassFixture<ImagiqaFactory>
{
    private readonly ImagiqaFactory _factory;

    public DemoTests(ImagiqaFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task TheHealthEndpoints_AnswerWithoutSigningIn()
    {
        using HttpClient client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using HttpResponseMessage live = await client.GetAsync(new Uri("/health/live", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
    }

    [PostgresFact]
    public async Task InDemoMode_EveryPage_SaysItIsNotForRealPatientData()
    {
        await ImagiqaFactory.MigrateAsync();
        SangamUser person = People.Person("Asha", People.At(Guid.NewGuid(), "doctor"));
        using DemoFactory demo = new();
        using HttpClient client = demo.ClientFor(person);
        Assert.Contains("Demo, not for real patient data", await client.GetStringAsync(new Uri("/", UriKind.Relative)), StringComparison.Ordinal);

        using HttpClient plain = _factory.ClientFor(person);
        Assert.DoesNotContain("Demo, not for real patient data", await plain.GetStringAsync(new Uri("/", UriKind.Relative)), StringComparison.Ordinal);
    }

    private sealed class DemoFactory : ImagiqaFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("Imagiqa:Demo", "true");
        }
    }
}
