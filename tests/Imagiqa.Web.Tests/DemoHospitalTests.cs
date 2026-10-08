using System.Net;
using System.Text.Json;
using Imagiqa.Web.Demo;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Sangam.Client;

namespace Imagiqa.Web.Tests;

/// <summary>V-15: on the demo, a tester with no role can join the made-up Demo Hospital in one click.</summary>
[Collection("imagiqa-db")]
public sealed class DemoHospitalTests : IClassFixture<ImagiqaFactory>
{
    private readonly ImagiqaFactory _factory;

    public DemoHospitalTests(ImagiqaFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Joining_GetsAManagementToken_ThenPutsTheRoles_TheHospital_AndTheMembership()
    {
        using Recorder recorder = new();
        using HttpClient http = new(recorder, disposeHandler: false);
        DemoHospital demo = new(http, Settings(demo: true));
        Guid person = Guid.NewGuid();

        Assert.Null(await demo.JoinAsync(person, "doctor"));

        Assert.Equal("POST https://id.example.in/connect/token", recorder.Calls[0].Line);
        Assert.Contains("grant_type=client_credentials", recorder.Calls[0].Body, StringComparison.Ordinal);
        Assert.Contains("scope=sangam.manage", recorder.Calls[0].Body, StringComparison.Ordinal);
        Assert.Equal(
            [
                "PUT https://id.example.in/api/v1/roles/doctor",
                "PUT https://id.example.in/api/v1/roles/nurse",
                $"PUT https://id.example.in/api/v1/orgs/{DemoHospital.DefaultId:D}",
                $"PUT https://id.example.in/api/v1/orgs/{DemoHospital.DefaultId:D}/members/{person:D}",
            ],
            recorder.Calls.Skip(1).Select(c => c.Line));
        Assert.All(recorder.Calls.Skip(1), c => Assert.Equal("Bearer t0ken", c.Authorization));
        using JsonDocument hospital = JsonDocument.Parse(recorder.Calls[3].Body);
        Assert.Equal(DemoHospital.Name, hospital.RootElement.GetProperty("name").GetString());
        Assert.Equal("hospital", hospital.RootElement.GetProperty("type").GetString());
        using JsonDocument membership = JsonDocument.Parse(recorder.Calls[4].Body);
        Assert.Equal("doctor", membership.RootElement.GetProperty("role").GetString());
    }

    [Fact]
    public async Task OutsideTheDemo_OrForAnotherRole_NothingIsSent()
    {
        using Recorder recorder = new();
        using HttpClient http = new(recorder, disposeHandler: false);
        Assert.NotNull(await new DemoHospital(http, Settings(demo: false)).JoinAsync(Guid.NewGuid(), "doctor"));
        Assert.NotNull(await new DemoHospital(http, Settings(demo: true)).JoinAsync(Guid.NewGuid(), "administrator"));
        Assert.Empty(recorder.Calls);
    }

    [Fact]
    public async Task ARefusalFromSangam_IsReported_NotHidden()
    {
        using Recorder recorder = new() { Refuse = "/members/" };
        using HttpClient http = new(recorder, disposeHandler: false);
        string? problem = await new DemoHospital(http, Settings(demo: true)).JoinAsync(Guid.NewGuid(), "nurse");
        Assert.NotNull(problem);
        Assert.Contains("/members/", problem, StringComparison.Ordinal);
    }

    [PostgresFact]
    public async Task OnTheDemo_SomeoneWithNoRole_IsOfferedTheDemoHospital()
    {
        await ImagiqaFactory.MigrateAsync();
        SangamUser nobody = People.Person("Ravi");
        using DemoOn demo = new();
        using HttpClient client = demo.ClientFor(nobody);
        string page = await client.GetStringAsync(new Uri("/", UriKind.Relative));
        Assert.Contains("You have no role at a hospital on imagiQa yet", page, StringComparison.Ordinal);
        Assert.Contains("Join as a doctor", page, StringComparison.Ordinal);
        Assert.Contains("action=\"/demo/join\"", page, StringComparison.Ordinal);

        using HttpClient plain = _factory.ClientFor(nobody);
        Assert.DoesNotContain("Join as a doctor", await plain.GetStringAsync(new Uri("/", UriKind.Relative)), StringComparison.Ordinal);
    }

    private static IConfiguration Settings(bool demo) => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Imagiqa:Demo"] = demo ? "true" : "false",
        ["Sangam:Authority"] = "https://id.example.in/",
        ["Sangam:ClientId"] = "imagiqa",
        ["Sangam:ClientSecret"] = "demo-secret",
    }).Build();

    private sealed class DemoOn : ImagiqaFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("Imagiqa:Demo", "true");
        }
    }

    private sealed class Recorder : HttpMessageHandler
    {
        public List<(string Line, string Body, string? Authorization)> Calls { get; } = [];

        public string? Refuse { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            Calls.Add(($"{request.Method} {request.RequestUri}", body, request.Headers.Authorization?.ToString()));
            if (Refuse is not null && request.RequestUri!.AbsolutePath.Contains(Refuse, StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            return request.RequestUri!.AbsolutePath == "/connect/token"
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"access_token\":\"t0ken\",\"token_type\":\"Bearer\"}") }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        }
    }
}
