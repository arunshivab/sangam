using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Sangam.Identity.Domain.Entities;
using Sangam.Identity.Infrastructure.Apps;
using Sangam.Identity.Infrastructure.Persistence;
using Sangam.Identity.Infrastructure.Seeding;

namespace Sangam.Identity.Server.Tests.Oidc;

/// <summary>
/// rc.15 (ASVS V2.10.1): an application signs in with a key it holds (<c>private_key_jwt</c>) instead of a secret. The
/// assertion must be for Sangam, short-lived and used once; a removed key, or a disabled application, signs in no more.
/// </summary>
[Collection("server")]
public sealed class ClientKeyTests
{
    private const string Audience = "http://localhost/";
    private readonly SangamServerFactory _factory;

    public ClientKeyTests(SangamServerFactory factory)
    {
        _factory = factory;
    }

    [PostgresFact]
    public async Task AnApplication_SignsInWithItsKey_OncePerAssertion()
    {
        using RSA rsa = RSA.Create(2048);
        (Guid keyRow, string kid) = await AddKeyAsync(rsa.ExportSubjectPublicKeyInfoPem());
        try
        {
            using HttpClient client = _factory.CreateClient();

            // The assertion must say what it is (typ client-authentication+jwt, draft-ietf-oauth-rfc7523bis); the issuer may
            // be written with or without its trailing slash.
            string assertion = Sign(rsa, kid, Audience, TimeSpan.FromMinutes(2));
            using (HttpResponseMessage ok = await TokenAsync(client, assertion))
            {
                Assert.True(ok.StatusCode == HttpStatusCode.OK, await ok.Content.ReadAsStringAsync());
            }

            using (HttpResponseMessage bare = await TokenAsync(client, Sign(rsa, kid, Audience.TrimEnd('/'), TimeSpan.FromMinutes(2))))
            {
                Assert.True(bare.StatusCode == HttpStatusCode.OK, await bare.Content.ReadAsStringAsync());
            }

            using (HttpResponseMessage plain = await TokenAsync(client, Sign(rsa, kid, Audience, TimeSpan.FromMinutes(2), "JWT")))
            {
                Assert.False(plain.IsSuccessStatusCode);
            }

            // The same assertion again is refused.
            using (HttpResponseMessage replay = await TokenAsync(client, assertion))
            {
                Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
                Assert.Contains("invalid_client", await replay.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            }

            // Not for Sangam's issuer (the token endpoint's address is not enough: draft-ietf-oauth-rfc7523bis), or too long-lived.
            using (HttpResponseMessage elsewhere = await TokenAsync(client, Sign(rsa, kid, "https://someone-else.example/", TimeSpan.FromMinutes(2))))
            {
                Assert.False(elsewhere.IsSuccessStatusCode);
            }

            using (HttpResponseMessage endpoint = await TokenAsync(client, Sign(rsa, kid, Audience + "connect/token", TimeSpan.FromMinutes(2))))
            {
                Assert.False(endpoint.IsSuccessStatusCode);
            }

            using (HttpResponseMessage longLived = await TokenAsync(client, Sign(rsa, kid, Audience, TimeSpan.FromHours(1))))
            {
                Assert.False(longLived.IsSuccessStatusCode);
            }

            // Expired a little while ago (OpenIddict alone would still allow it for five minutes), or made to last a day and
            // used near its end: both refused.
            using (HttpResponseMessage expired = await TokenAsync(client, SignAt(rsa, kid, DateTime.UtcNow.AddMinutes(-5), DateTime.UtcNow.AddMinutes(-3))))
            {
                Assert.False(expired.IsSuccessStatusCode);
            }

            using (HttpResponseMessage dayLong = await TokenAsync(client, SignAt(rsa, kid, DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddMinutes(2))))
            {
                Assert.False(dayLong.IsSuccessStatusCode);
            }

            // Signed with another key.
            using (RSA other = RSA.Create(2048))
            using (HttpResponseMessage forged = await TokenAsync(client, Sign(other, kid, Audience, TimeSpan.FromMinutes(2))))
            {
                Assert.False(forged.IsSuccessStatusCode);
            }

            using (IServiceScope scope = _factory.Services.CreateScope())
            {
                SangamDbContext db = scope.ServiceProvider.GetRequiredService<SangamDbContext>();
                Assert.NotNull((await db.ClientKeys.AsNoTracking().SingleAsync(k => k.Id == keyRow)).LastUsedAt);

                // Removed: the key no longer works.
                await db.ClientKeys.Where(k => k.Id == keyRow).ExecuteUpdateAsync(u => u.SetProperty(k => k.RevokedAt, DateTimeOffset.UtcNow));
            }

            using HttpResponseMessage removed = await TokenAsync(client, Sign(rsa, kid, Audience, TimeSpan.FromMinutes(2)));
            Assert.False(removed.IsSuccessStatusCode);
        }
        finally
        {
            await RemoveKeyAsync(keyRow);
        }
    }

    [PostgresFact]
    public async Task AnEllipticCurveKey_Works_And_DiscoveryAdvertisesKeys()
    {
        using ECDsa ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        (Guid keyRow, string kid) = await AddKeyAsync(ec.ExportSubjectPublicKeyInfoPem());
        try
        {
            using HttpClient client = _factory.CreateClient();
            SigningCredentials credentials = new(new ECDsaSecurityKey(ec) { KeyId = kid }, SecurityAlgorithms.EcdsaSha256);
            using HttpResponseMessage ok = await TokenAsync(client, Sign(credentials, Audience, TimeSpan.FromMinutes(2)));
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);

            using JsonDocument discovery = JsonDocument.Parse(await client.GetStringAsync(new Uri("/.well-known/openid-configuration", UriKind.Relative)));
            Assert.Contains(
                "private_key_jwt",
                discovery.RootElement.GetProperty("token_endpoint_auth_methods_supported").EnumerateArray().Select(e => e.GetString()));
        }
        finally
        {
            await RemoveKeyAsync(keyRow);
        }
    }

    [PostgresFact]
    public async Task TheDotNetSdk_SignsAssertions_SangamAccepts_WithTheKeyIdTheConsoleShows()
    {
        using ECDsa ec = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        (Guid keyRow, string kid) = await AddKeyAsync(ec.ExportSubjectPublicKeyInfoPem());
        try
        {
            using Sangam.Client.SangamClientAssertion sdk = Sangam.Client.SangamClientAssertion.FromPem(ec.ExportPkcs8PrivateKeyPem(), DevelopmentSeeder.SampleClientId, "http://localhost");
            Assert.Equal(kid, sdk.KeyId);

            using HttpClient client = _factory.CreateClient();
            using HttpResponseMessage first = await TokenAsync(client, sdk.Create());
            Assert.True(first.StatusCode == HttpStatusCode.OK, await first.Content.ReadAsStringAsync());
            using HttpResponseMessage second = await TokenAsync(client, sdk.Create());
            Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        }
        finally
        {
            await RemoveKeyAsync(keyRow);
        }
    }

    private async Task<(Guid Row, string Kid)> AddKeyAsync(string pem)
    {
        ClientKeyParse parsed = ClientKeyFormat.Parse(pem);
        Assert.True(parsed.Succeeded, parsed.Message);
        using IServiceScope scope = _factory.Services.CreateScope();
        SangamDbContext db = scope.ServiceProvider.GetRequiredService<SangamDbContext>();
        Guid appId = await db.Apps.Where(a => a.ClientId == DevelopmentSeeder.SampleClientId).Select(a => a.Id).FirstAsync();
        ClientKey key = new()
        {
            Id = Guid.NewGuid(),
            AppId = appId,
            KeyId = parsed.KeyId!,
            Jwk = parsed.Jwk!,
            Kind = parsed.Kind!,
            Name = "test",
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = Guid.NewGuid(),
        };
        db.ClientKeys.Add(key);
        await db.SaveChangesAsync();
        return (key.Id, key.KeyId);
    }

    private async Task RemoveKeyAsync(Guid id)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        SangamDbContext db = scope.ServiceProvider.GetRequiredService<SangamDbContext>();
        await db.ClientKeys.Where(k => k.Id == id).ExecuteDeleteAsync();
    }

    private static string Sign(RSA rsa, string kid, string audience, TimeSpan lifetime, string type = "client-authentication+jwt")
        => Sign(new SigningCredentials(new RsaSecurityKey(rsa) { KeyId = kid }, SecurityAlgorithms.RsaSha256), audience, lifetime, type);

    private static string Sign(SigningCredentials credentials, string audience, TimeSpan lifetime, string type = "client-authentication+jwt")
    {
        DateTime now = DateTime.UtcNow;
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = DevelopmentSeeder.SampleClientId,
            Audience = audience,
            Claims = new Dictionary<string, object>
            {
                ["sub"] = DevelopmentSeeder.SampleClientId,
                ["jti"] = Guid.NewGuid().ToString("N"),
            },
            IssuedAt = now,
            NotBefore = now,
            Expires = now + lifetime,
            TokenType = type,
            SigningCredentials = credentials,
        });
    }

    private static string SignAt(RSA rsa, string kid, DateTime issuedAt, DateTime expires)
        => new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = DevelopmentSeeder.SampleClientId,
            Audience = Audience,
            Claims = new Dictionary<string, object>
            {
                ["sub"] = DevelopmentSeeder.SampleClientId,
                ["jti"] = Guid.NewGuid().ToString("N"),
            },
            IssuedAt = issuedAt,
            NotBefore = issuedAt,
            Expires = expires,
            TokenType = "client-authentication+jwt",
            SigningCredentials = new SigningCredentials(new RsaSecurityKey(rsa) { KeyId = kid }, SecurityAlgorithms.RsaSha256),
        });

    private static async Task<HttpResponseMessage> TokenAsync(HttpClient client, string assertion)
    {
        using FormUrlEncodedContent form = new(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = DevelopmentSeeder.SampleClientId,
            ["client_assertion_type"] = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer",
            ["client_assertion"] = assertion,
            ["scope"] = "sangam.manage",
        });
        return await client.PostAsync(new Uri("/connect/token", UriKind.Relative), form);
    }
}
