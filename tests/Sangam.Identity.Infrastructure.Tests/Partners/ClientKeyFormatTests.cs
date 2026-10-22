using System.Security.Cryptography;
using System.Text.Json;
using Sangam.Identity.Infrastructure.Apps;

namespace Sangam.Identity.Infrastructure.Tests.Partners;

/// <summary>rc.15 (ASVS V2.10.1): the public keys an owner may paste, and the ones refused.</summary>
public sealed class ClientKeyFormatTests
{
    [Fact]
    public void APemOrAJwk_OfTheSameKey_GiveTheSameKeyId()
    {
        using RSA rsa = RSA.Create(2048);
        ClientKeyParse pem = ClientKeyFormat.Parse(rsa.ExportSubjectPublicKeyInfoPem());
        ClientKeyParse pkcs1 = ClientKeyFormat.Parse(rsa.ExportRSAPublicKeyPem());
        Assert.True(pem.Succeeded, pem.Message);
        Assert.True(pkcs1.Succeeded, pkcs1.Message);
        Assert.Equal(pem.KeyId, pkcs1.KeyId);
        Assert.Equal("RSA 2048", pem.Kind);

        // The stored key has no private part, and reads back as the same key.
        using JsonDocument stored = JsonDocument.Parse(pem.Jwk!);
        Assert.False(stored.RootElement.TryGetProperty("d", out _));
        Assert.Equal(pem.KeyId, stored.RootElement.GetProperty("kid").GetString());
        ClientKeyParse again = ClientKeyFormat.Parse(pem.Jwk);
        Assert.Equal(pem.KeyId, again.KeyId);
        ClientKeyParse inSet = ClientKeyFormat.Parse("{\"keys\":[" + pem.Jwk + "]}");
        Assert.Equal(pem.KeyId, inSet.KeyId);
    }

    [Fact]
    public void TheKeyId_IsTheRfc7638Thumbprint()
    {
        // The example key of RFC 7638 section 3.1.
        const string Jwk = "{\"kty\":\"RSA\",\"n\":\"0vx7agoebGcQSuuPiLJXZptN9nndrQmbXEps2aiAFbWhM78LhWx4cbbfAAtVT86zwu1RK7aPFFxuhDR1L6tSoc_BJECPebWKRXjBZCiFV4n3oknjhMstn64tZ_2W-5JsGY4Hc5n9yBXArwl93lqt7_RN5w6Cf0h4QyQ5v-65YGjQR0_FDW2QvzqY368QQMicAtaSqzs8KJZgnYb9c7d0zgdAZHzu6qMQvRL5hajrn1n91CbOpbISD08qNLyrdkt-bFTWhAI4vMQFh6WeZu0fM4lFd2NcRwr3XPksINHaQ-G_xBniIqbw0Ls1jF44-csFCur-kEgU8awapJzKnqDKgw\",\"e\":\"AQAB\",\"alg\":\"RS256\",\"kid\":\"2011-04-29\"}";
        ClientKeyParse parsed = ClientKeyFormat.Parse(Jwk);
        Assert.True(parsed.Succeeded, parsed.Message);
        Assert.Equal("NzbLsXh8uDCcd-6MNwXF4W_7noWXFZAfHkxZsRGC9Xs", parsed.KeyId);
    }

    [Fact]
    public void EllipticCurveKeys_AreAccepted_OnTheNistCurves()
    {
        using ECDsa p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        ClientKeyParse parsed = ClientKeyFormat.Parse(p384.ExportSubjectPublicKeyInfoPem());
        Assert.True(parsed.Succeeded, parsed.Message);
        Assert.Equal("EC P-384", parsed.Kind);
        Assert.Equal(parsed.KeyId, ClientKeyFormat.Parse(parsed.Jwk).KeyId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a key")]
    [InlineData("{\"kty\":\"oct\",\"k\":\"c2VjcmV0\"}")]
    [InlineData("{\"kty\":\"RSA\",\"n\":\"AQAB\",\"e\":\"AQAB\",\"use\":\"enc\"}")]
    [InlineData("{\"keys\":[]}")]
    public void ThingsThatAreNotAPublicSigningKey_AreRefused(string text)
    {
        Assert.False(ClientKeyFormat.Parse(text).Succeeded);
    }

    [Fact]
    public void PrivateAndWeakKeys_AreRefused()
    {
        using RSA rsa = RSA.Create(2048);
        Assert.Contains("private key", ClientKeyFormat.Parse(rsa.ExportPkcs8PrivateKeyPem()).Message, StringComparison.Ordinal);
        Assert.Contains("private key", ClientKeyFormat.Parse(rsa.ExportRSAPrivateKeyPem()).Message, StringComparison.Ordinal);

        RSAParameters full = rsa.ExportParameters(true);
        string privateJwk = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["kty"] = "RSA",
            ["n"] = Base64Url(full.Modulus!),
            ["e"] = Base64Url(full.Exponent!),
            ["d"] = Base64Url(full.D!),
        });
        Assert.Contains("private key", ClientKeyFormat.Parse(privateJwk).Message, StringComparison.Ordinal);

        using RSA weak = RSA.Create(1024);
        Assert.Contains("1024 bits", ClientKeyFormat.Parse(weak.ExportSubjectPublicKeyInfoPem()).Message, StringComparison.Ordinal);

        using ECDsa secp256k1 = ECDsa.Create(ECCurve.CreateFromFriendlyName("secp256k1"));
        Assert.False(ClientKeyFormat.Parse(secp256k1.ExportSubjectPublicKeyInfoPem()).Succeeded);
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
