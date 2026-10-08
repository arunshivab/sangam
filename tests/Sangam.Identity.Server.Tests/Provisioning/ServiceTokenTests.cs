using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Sangam.Identity.Infrastructure.Provisioning;
using Sangam.Identity.Server.Provisioning;

namespace Sangam.Identity.Server.Tests.Provisioning;

/// <summary>
/// PR-23: in the <c>sangam</c> mode an application's SCIM server checks Sangam's token against Sangam's published keys,
/// as it checks access tokens — no secret is shared. The token is for that one SCIM address and lasts five minutes.
/// </summary>
[Collection("server")]
public sealed class ServiceTokenTests
{
    private readonly SangamServerFactory _factory;

    public ServiceTokenTests(SangamServerFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task ASangamSignedScimToken_ValidatesAgainstTheJwks_ForThatAddressOnly()
    {
        const string Scim = "https://lims.example.in/scim/v2";
        string token = _factory.Services.GetRequiredService<IServiceTokenIssuer>().Issue(Scim, TimeSpan.FromMinutes(5));

        using HttpClient client = _factory.CreateClient();
        JsonElement discovery = JsonDocument.Parse(await client.GetStringAsync(new Uri("/.well-known/openid-configuration", UriKind.Relative))).RootElement;
        JsonWebKeySet keys = new(await client.GetStringAsync(new Uri(discovery.GetProperty("jwks_uri").GetString()!)));
        TokenValidationParameters rules = new()
        {
            ValidIssuer = discovery.GetProperty("issuer").GetString(),
            ValidAudience = Scim,
            IssuerSigningKeys = keys.GetSigningKeys(),
            ValidTypes = [ServiceTokenIssuer.TokenType],
        };
        TokenValidationResult valid = await new JsonWebTokenHandler().ValidateTokenAsync(token, rules);
        Assert.True(valid.IsValid, valid.Exception?.Message);
        Assert.Equal(ServiceTokenIssuer.Subject, valid.Claims["sub"]);
        JsonWebToken parsed = new(token);
        Assert.InRange(parsed.ValidTo - parsed.ValidFrom, TimeSpan.FromMinutes(4), TimeSpan.FromMinutes(5));

        // For another application's SCIM address it is worthless.
        rules.ValidAudience = "https://other.example.in/scim/v2";
        Assert.False((await new JsonWebTokenHandler().ValidateTokenAsync(token, rules)).IsValid);
    }
}
