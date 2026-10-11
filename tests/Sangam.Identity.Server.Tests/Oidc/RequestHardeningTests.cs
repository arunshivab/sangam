using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Sangam.Identity.Infrastructure.Seeding;

namespace Sangam.Identity.Server.Tests.Oidc;

/// <summary>
/// rc.8 request hardening: a protocol parameter sent twice is refused (ASVS V5.1.1, parameter pollution); the management API
/// takes only JSON bodies and answers only in JSON (V13.1.5, V13.2.5); only the HTTP methods Sangam uses are answered (V14.5.1).
/// </summary>
[Collection("server")]
public sealed class RequestHardeningTests
{
    private readonly SangamServerFactory _factory;

    public RequestHardeningTests(SangamServerFactory factory)
    {
        _factory = factory;
    }

    [PostgresFact]
    public async Task AParameterSentTwice_ToTheTokenEndpoint_IsRefused()
    {
        using HttpClient client = _factory.CreateClient();
        using FormUrlEncodedContent form = new(
        [
            new("grant_type", "client_credentials"),
            new("client_id", DevelopmentSeeder.SampleClientId),
            new("client_secret", DevelopmentSeeder.SampleClientSecret),
            new("scope", "sangam.manage"),
            new("scope", "openid"),
        ]);
        using HttpResponseMessage response = await client.PostAsync(new Uri("/connect/token", UriKind.Relative), form);
        string body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("invalid_request", body, StringComparison.Ordinal);
    }

    [PostgresFact]
    public async Task AParameterSentTwice_ToTheAuthorizeEndpoint_IsRefused()
    {
        using HttpClient client = _factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using HttpResponseMessage response = await client.GetAsync(new Uri(
            "/connect/authorize?response_type=code&client_id=" + DevelopmentSeeder.SampleClientId
            + "&client_id=someone-else&redirect_uri=https%3A%2F%2Flocalhost%3A5101%2Fsignin-oidc&scope=openid",
            UriKind.Relative));
        string body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("invalid_request", body, StringComparison.Ordinal);
    }

    [PostgresFact]
    public async Task TheManagementApi_RefusesABodyThatIsNotJson_AndAnAcceptItCannotMeet()
    {
        using HttpClient client = _factory.CreateClient();
        string token = await ClientTokenAsync(client);

        using HttpRequestMessage xml = new(HttpMethod.Put, "/api/v1/roles/rc8-hardening")
        {
            Content = new StringContent("<role><name>x</name></role>", Encoding.UTF8, "application/xml"),
        };
        xml.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using HttpResponseMessage unsupported = await client.SendAsync(xml);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, unsupported.StatusCode);

        using HttpRequestMessage wantsXml = new(HttpMethod.Get, "/api/v1/roles");
        wantsXml.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        wantsXml.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/xml"));
        using HttpResponseMessage notAcceptable = await client.SendAsync(wantsXml);
        Assert.Equal(HttpStatusCode.NotAcceptable, notAcceptable.StatusCode);

        // JSON, or any type at all, is still answered.
        using HttpRequestMessage anything = new(HttpMethod.Get, "/api/v1/roles");
        anything.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        anything.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/xml"));
        anything.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*", 0.1));
        using HttpResponseMessage ok = await client.SendAsync(anything);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
    }

    [PostgresFact]
    public async Task AMethodSangamDoesNotUse_IsRefused_AsIsTheWrongMethodForAnAddress()
    {
        using HttpClient client = _factory.CreateClient();
        using HttpRequestMessage trace = new(new HttpMethod("TRACE"), "/");
        using HttpResponseMessage refused = await client.SendAsync(trace);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, refused.StatusCode);
        Assert.DoesNotContain("TRACE", refused.Content.Headers.Allow);

        using HttpRequestMessage wrong = new(HttpMethod.Delete, "/api/v1/roles");
        using HttpResponseMessage notHere = await client.SendAsync(wrong);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, notHere.StatusCode);
    }

    private static async Task<string> ClientTokenAsync(HttpClient client)
    {
        using FormUrlEncodedContent form = new(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = DevelopmentSeeder.SampleClientId,
            ["client_secret"] = DevelopmentSeeder.SampleClientSecret,
            ["scope"] = "sangam.manage",
        });
        using HttpResponseMessage response = await client.PostAsync(new Uri("/connect/token", UriKind.Relative), form);
        response.EnsureSuccessStatusCode();
        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("access_token").GetString()!;
    }
}
