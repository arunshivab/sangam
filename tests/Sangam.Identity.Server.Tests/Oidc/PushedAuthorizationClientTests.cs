using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Abstractions;
using Sangam.Identity.Infrastructure.Seeding;

namespace Sangam.Identity.Server.Tests.Oidc;

/// <summary>
/// Found in the R4 browser run: once discovery advertises a PAR endpoint, ASP.NET Core's OpenID Connect handler — and
/// so Sangam.Client, the consoles, the portal and imagiQa — pushes every sign-in there first. A client without the
/// PAR permission then could not sign anyone in. Every client that signs people in must be allowed to push.
/// </summary>
[Collection("server")]
public sealed class PushedAuthorizationClientTests
{
    private readonly SangamServerFactory _factory;

    public PushedAuthorizationClientTests(SangamServerFactory factory)
    {
        _factory = factory;
    }

    [PostgresFact]
    public async Task EveryClientThatSignsPeopleIn_MayPushItsRequest()
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        IOpenIddictApplicationManager manager = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
        List<string> missing = [];
        int checkedClients = 0;
        await foreach (object application in manager.ListAsync())
        {
            System.Collections.Immutable.ImmutableArray<string> permissions = await manager.GetPermissionsAsync(application);
            if (permissions.Contains(OpenIddictConstants.Permissions.Endpoints.Authorization))
            {
                checkedClients++;
                if (!permissions.Contains(OpenIddictConstants.Permissions.Endpoints.PushedAuthorization))
                {
                    missing.Add(await manager.GetClientIdAsync(application) ?? "?");
                }
            }
        }

        Assert.True(checkedClients >= 4, $"only {checkedClients} clients checked");
        Assert.Empty(missing);
    }

    [PostgresFact]
    public async Task TheSigninTheDotNetHandlerMakes_IsAcceptedAtTheParEndpoint()
    {
        // The request ASP.NET Core's handler pushes for a confidential client: its parameters, authenticated by its secret.
        using HttpClient client = _factory.CreateClient();
        using FormUrlEncodedContent form = new(new Dictionary<string, string>
        {
            ["client_id"] = DevelopmentSeeder.AdminClientId,
            ["client_secret"] = DevelopmentSeeder.AdminClientSecret,
            ["response_type"] = "code",
            ["scope"] = "openid profile",
            ["redirect_uri"] = "http://localhost:5300/signin-sangam",
            ["code_challenge"] = "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM",
            ["code_challenge_method"] = "S256",
            ["state"] = "s",
            ["nonce"] = "n",
        });
        using HttpResponseMessage response = await client.PostAsync(new Uri("/connect/par", UriKind.Relative), form);
        string body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Created, body);
        Assert.StartsWith("urn:ietf:params:oauth:request_uri:", JsonDocument.Parse(body).RootElement.GetProperty("request_uri").GetString(), StringComparison.Ordinal);
    }
}
