using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.JsonWebTokens;

namespace Sangam.Client.Tests;

/// <summary>rc.15: signing in with a key instead of a secret (<c>private_key_jwt</c>).</summary>
public sealed class ClientAssertionTests
{
    [Fact]
    public void AnAssertion_IsForSangam_ShortLived_Typed_AndNeverTheSameTwice()
    {
        using RSA rsa = RSA.Create(2048);
        using SangamClientAssertion maker = SangamClientAssertion.FromPem(rsa.ExportPkcs8PrivateKeyPem(), "lims", "https://id.example.in");

        JsonWebToken first = new(maker.Create());
        JsonWebToken second = new(maker.Create());
        Assert.Equal("lims", first.Issuer);
        Assert.Equal("lims", first.Subject);
        Assert.Equal("https://id.example.in/", Assert.Single(first.Audiences));
        Assert.Equal("client-authentication+jwt", first.Typ);
        Assert.Equal("RS256", first.Alg);
        Assert.Equal(maker.KeyId, first.Kid);
        Assert.True(first.ValidTo - first.ValidFrom <= TimeSpan.FromMinutes(2));
        Assert.NotEqual(first.Id, second.Id);
        Assert.False(string.IsNullOrEmpty(first.Id));
    }

    [Fact]
    public void TheKeyId_IsTheThumbprint_TheConsoleShows()
    {
        // RFC 7638: SHA-256 of {"e","kty","n"} in that order, base64url.
        using RSA rsa = RSA.Create(2048);
        RSAParameters p = rsa.ExportParameters(false);
        string canonical = $"{{\"e\":\"{B64(p.Exponent!)}\",\"kty\":\"RSA\",\"n\":\"{B64(p.Modulus!)}\"}}";
        using SangamClientAssertion maker = SangamClientAssertion.FromPem(rsa.ExportRSAPrivateKeyPem(), "lims", "https://id.example.in");
        Assert.Equal(B64(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))), maker.KeyId);

        using ECDsa ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using SangamClientAssertion ecMaker = SangamClientAssertion.FromPem(ec.ExportPkcs8PrivateKeyPem(), "lims", "https://id.example.in");
        Assert.Equal("ES256", new JsonWebToken(ecMaker.Create()).Alg);
    }

    [Fact]
    public void APublicKey_OrAWeakKey_IsRefusedAtStartUp()
    {
        using RSA rsa = RSA.Create(2048);
        Assert.Throws<InvalidOperationException>(() => SangamClientAssertion.FromPem(rsa.ExportSubjectPublicKeyInfoPem(), "lims", "https://id.example.in"));
        using RSA weak = RSA.Create(1024);
        Assert.Throws<InvalidOperationException>(() => SangamClientAssertion.FromPem(weak.ExportPkcs8PrivateKeyPem(), "lims", "https://id.example.in"));
        Assert.Throws<InvalidOperationException>(() => new SangamOptions { Authority = "https://id.example.in", ClientId = "lims", PrivateKeyPem = "x", PrivateKeyPath = "y" }.Validate());
        Assert.Throws<InvalidOperationException>(() => new SangamOptions { Authority = "https://id.example.in", ClientId = "lims", PrivateKeyPath = "/no/such/key.pem" }.Validate());
        Assert.Null(SangamClientAssertion.From(new SangamOptions { Authority = "https://id.example.in", ClientId = "lims", ClientSecret = "s" }));
    }

    [Fact]
    public async Task TheManagementClient_SendsAnAssertion_AndNoSecret()
    {
        using RSA rsa = RSA.Create(2048);
        string path = Path.Combine(Path.GetTempPath(), "sangam-key-" + Guid.NewGuid().ToString("N") + ".pem");
        await File.WriteAllTextAsync(path, rsa.ExportPkcs8PrivateKeyPem());
        try
        {
            using Capturing handler = new();
            SangamOptions options = new() { Authority = "https://id.example.in/", ClientId = "lims", PrivateKeyPath = path };
            options.Validate();
            using SangamManagementClient client = new(new Factory(handler), options);
            Assert.Equal("t1", await client.GetTokenAsync());
            Assert.Contains("client_assertion_type=urn%3Aietf%3Aparams%3Aoauth%3Aclient-assertion-type%3Ajwt-bearer", handler.Body, StringComparison.Ordinal);
            Assert.Contains("client_assertion=ey", handler.Body, StringComparison.Ordinal);
            Assert.DoesNotContain("client_secret", handler.Body, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string B64(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed class Capturing : HttpMessageHandler
    {
        public string Body { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"access_token\":\"t1\",\"expires_in\":900}") };
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
