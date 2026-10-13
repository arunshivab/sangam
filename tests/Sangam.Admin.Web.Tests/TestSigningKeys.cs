using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.IdentityModel.Tokens;
using Sangam.Admin.Web.Components.Common;

namespace Sangam.Admin.Web.Tests;

/// <summary>rc.11: a published key set of two certificates, the newer listed first, as OpenIddict lists them.</summary>
internal sealed class TestSigningKeys : ISigningKeySource
{
    public Task<SigningKeysResult> ReadAsync(CancellationToken cancellationToken = default)
    {
        using X509Certificate2 current = Certificate("CN=Sangam signing 2026", DateTimeOffset.UtcNow.AddDays(-30), DateTimeOffset.UtcNow.AddDays(700));
        using X509Certificate2 previous = Certificate("CN=Sangam signing 2025", DateTimeOffset.UtcNow.AddDays(-400), DateTimeOffset.UtcNow.AddDays(20));
        JsonWebKey[] keys = [Jwk(current), Jwk(previous)];
        return Task.FromResult(new SigningKeysResult(OidcSigningKeySource.FromKeySet(keys), null));
    }

    private static JsonWebKey Jwk(X509Certificate2 certificate)
    {
        JsonWebKey key = JsonWebKeyConverter.ConvertFromX509SecurityKey(new X509SecurityKey(certificate), representAsRsaKey: true);
        key.Use = JsonWebKeyUseNames.Sig;
        key.Alg = SecurityAlgorithms.RsaSha256;
        key.X5c.Add(Convert.ToBase64String(certificate.RawData));
        return key;
    }

    private static X509Certificate2 Certificate(string subject, DateTimeOffset from, DateTimeOffset until)
    {
        using RSA rsa = RSA.Create(2048);
        CertificateRequest request = new(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(from, until);
    }
}
