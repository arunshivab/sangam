using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Sangam.Identity.Infrastructure.Seeding;

namespace Sangam.Identity.Server.Tests;

/// <summary>The first protocol endpoints: discovery, JWKS and the client-credentials grant.</summary>
[Collection("server")]
public sealed class OpenIdConnectTests
{
    private readonly SangamServerFactory _factory;

    public OpenIdConnectTests(SangamServerFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Discovery_AdvertisesTokenEndpointJwksAndScopes()
    {
        using HttpClient client = _factory.CreateClient();

        JsonElement doc = await client.GetFromJsonAsync<JsonElement>(new Uri("/.well-known/openid-configuration", UriKind.Relative));

        Assert.Equal("http://localhost/", doc.GetProperty("issuer").GetString());
        Assert.Equal("http://localhost/connect/token", doc.GetProperty("token_endpoint").GetString());
        Assert.Equal("http://localhost/.well-known/jwks", doc.GetProperty("jwks_uri").GetString());
        Assert.Contains("client_credentials", doc.GetProperty("grant_types_supported").EnumerateArray().Select(e => e.GetString()));
        Assert.Contains("orgs.read", doc.GetProperty("scopes_supported").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task Jwks_PublishesOneRsaSigningKey()
    {
        using HttpClient client = _factory.CreateClient();

        JsonElement jwks = await client.GetFromJsonAsync<JsonElement>(new Uri("/.well-known/jwks", UriKind.Relative));

        JsonElement[] keys = [.. jwks.GetProperty("keys").EnumerateArray()];
        Assert.Single(keys);
        Assert.Equal("RSA", keys[0].GetProperty("kty").GetString());
        Assert.Equal("sig", keys[0].GetProperty("use").GetString());
        Assert.False(keys[0].TryGetProperty("d", out _), "JWKS must never expose the private exponent.");
    }

    [PostgresFact]
    public async Task Token_ClientCredentials_IssuesAccessTokenForSeededApp()
    {
        using HttpClient client = _factory.CreateClient();
        using FormUrlEncodedContent form = new(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = DevelopmentSeeder.SampleClientId,
            ["client_secret"] = DevelopmentSeeder.SampleClientSecret,
            ["scope"] = "orgs.read",
        });

        using HttpResponseMessage response = await client.PostAsync(new Uri("/connect/token", UriKind.Relative), form);
        string body = await response.Content.ReadAsStringAsync();

        Assert.True(response.IsSuccessStatusCode, body);
        JsonElement json = JsonDocument.Parse(body).RootElement;
        Assert.Equal("Bearer", json.GetProperty("token_type").GetString());
        string token = json.GetProperty("access_token").GetString()!;
        Assert.Equal(3, token.Split('.').Length);

        JsonElement payload = DecodePayload(token);
        Assert.Equal(DevelopmentSeeder.SampleClientId, payload.GetProperty("sub").GetString());
        Assert.Equal("orgs.read", payload.GetProperty("scope").GetString());
    }

    [PostgresFact]
    public async Task Token_ClientCredentials_RejectsWrongSecret()
    {
        using HttpClient client = _factory.CreateClient();
        using FormUrlEncodedContent form = new(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = DevelopmentSeeder.SampleClientId,
            ["client_secret"] = "definitely-wrong",
        });

        using HttpResponseMessage response = await client.PostAsync(new Uri("/connect/token", UriKind.Relative), form);
        string body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("invalid_client", body, StringComparison.Ordinal);
    }

    private static JsonElement DecodePayload(string jwt)
    {
        string payload = jwt.Split('.')[1];
        payload = payload.Replace('-', '+').Replace('_', '/');
        payload = payload.PadRight(payload.Length + ((4 - (payload.Length % 4)) % 4), '=');
        return JsonDocument.Parse(Convert.FromBase64String(payload)).RootElement;
    }
}
