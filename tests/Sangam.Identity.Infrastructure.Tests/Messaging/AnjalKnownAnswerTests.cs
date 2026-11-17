using System.Security.Cryptography;
using System.Text;
using Sangam.Identity.Infrastructure.Messaging;

namespace Sangam.Identity.Infrastructure.Tests.Messaging;

/// <summary>
/// Anjal's signed-request known-answer vector (TEST ONLY key), produced by Anjal's own verifier and pinned in Anjal's
/// tests (tests/Anjal.Webmail.Tests/Rc26AppApiVectorTests.cs), relayed on 18 November 2026. Anjal's real API answered
/// 202 to the request and 401 <c>signature_invalid</c> when the full stop in <c>"Test only."</c> became <c>!</c>. That
/// byte is at 0-based offset 119 (the 120th of 122, just before the closing <c>"}</c>); the relayed note said 120, which
/// is the closing quote. ECDSA signatures are randomised, so Sangam cannot reproduce Anjal's signature byte for byte; it checks that
/// it builds the same signing string and key id, that Anjal's signature verifies over that string and fails over the
/// changed body, and that a signature Sangam makes over the same request verifies the same way.
/// </summary>
public sealed class AnjalKnownAnswerTests
{
    private const string PublicKeyPem = """
        -----BEGIN PUBLIC KEY-----
        MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEGdiauyEJ/xWEd5K4Q7qW7zKmA9cn
        H4oUWg69E60A90V5HUH+dXlOrYlRFtu5ouVjdclaepAyhetrzVPTTFyMuQ==
        -----END PUBLIC KEY-----
        """;

    private const string KeyId = "sk_45bea211c2d5";
    private const string SpkiSha256 = "45bea211c2d575e3497b908178107b9175bc7136feec15c8f119f3c318197fbb";
    private const string Target = "/api/v1/messages/email";
    private const string Timestamp = "1763380800";
    private const string Body = "{\"from\":{\"email\":\"no-reply@sgh.in\"},\"to\":[{\"email\":\"priya@gmail.test\"}],\"subject\":\"Known-answer test\",\"text\":\"Test only.\"}";
    private const string BodySha256 = "953c6d38e8c84ff262466f96bcc01678bc58611b56201ca245b0ad642195e7e6";
    private const string Signature = "G0DnL6Mma2YJGdATtNJPa6w5vlDGvAMnypgFhpdfj-iuNzSZgUtTK2K5-GZ-erF6xpb7TcSvwyhOELNFpbq_qw";

    private const string SigningString = "POST\n" + Target + "\n" + Timestamp + "\n" + BodySha256;

    [Fact]
    public void TheBody_Is122Bytes_WithTheVectorsHash()
    {
        byte[] body = Encoding.UTF8.GetBytes(Body);
        Assert.Equal(122, body.Length);
        Assert.Equal(BodySha256, Convert.ToHexStringLower(SHA256.HashData(body)));
    }

    [Fact]
    public void SangamBuildsTheSameSigningString()
    {
        string text = AnjalRequestSigner.SigningString("POST", Target, Timestamp, Encoding.UTF8.GetBytes(Body));
        Assert.Equal(SigningString, text);
    }

    [Fact]
    public void SangamGivesThePublicKeyTheSameKeyId()
    {
        using ECDsa key = ECDsa.Create();
        key.ImportFromPem(PublicKeyPem);
        Assert.Equal(SpkiSha256, Convert.ToHexStringLower(SHA256.HashData(key.ExportSubjectPublicKeyInfo())));
        Assert.Equal(KeyId, AnjalRequestSigner.KeyIdOf(key));
    }

    [Fact]
    public void AnjalsSignature_Verifies_OverTheSigningString()
    {
        using ECDsa key = ECDsa.Create();
        key.ImportFromPem(PublicKeyPem);
        Assert.True(Verify(key, SigningString, Signature));
    }

    [Fact]
    public void AnjalsSignature_Fails_WhenOneBodyByteChanges()
    {
        byte[] body = Encoding.UTF8.GetBytes(Body);
        Assert.Equal((byte)'.', body[119]);
        body[119] = (byte)'!';
        Assert.EndsWith("\"Test only!\"}", Encoding.UTF8.GetString(body), StringComparison.Ordinal);

        string changed = AnjalRequestSigner.SigningString("POST", Target, Timestamp, body);
        using ECDsa key = ECDsa.Create();
        key.ImportFromPem(PublicKeyPem);
        Assert.False(Verify(key, changed, Signature));
    }

    [Fact]
    public void ASignatureSangamMakes_OverTheSameRequest_VerifiesTheSameWay()
    {
        using ECDsa fresh = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using AnjalRequestSigner signer = AnjalRequestSigner.FromPem(fresh.ExportPkcs8PrivateKeyPem());
        using HttpRequestMessage request = new(HttpMethod.Post, new Uri("https://mail.anjal.co.in" + Target));
        signer.Sign(request, Encoding.UTF8.GetBytes(Body), DateTimeOffset.FromUnixTimeSeconds(long.Parse(Timestamp, System.Globalization.CultureInfo.InvariantCulture)));

        Assert.Equal(Timestamp, request.Headers.GetValues(AnjalRequestSigner.TimestampHeader).Single());
        Assert.Equal(AnjalRequestSigner.KeyIdOf(fresh), request.Headers.GetValues(AnjalRequestSigner.KeyIdHeader).Single());
        string signature = request.Headers.GetValues(AnjalRequestSigner.SignatureHeader).Single();
        Assert.NotEqual(Signature, signature);
        Assert.True(Verify(fresh, SigningString, signature));
    }

    private static bool Verify(ECDsa key, string signingString, string base64UrlSignature)
    {
        byte[] signature = Convert.FromBase64String(base64UrlSignature.Replace('-', '+').Replace('_', '/') + "==");
        return signature.Length == 64
            && key.VerifyData(Encoding.UTF8.GetBytes(signingString), signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }
}
