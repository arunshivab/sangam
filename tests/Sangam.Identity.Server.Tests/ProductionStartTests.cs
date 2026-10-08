using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Sangam.Identity.Infrastructure;
using Sangam.Identity.Server.Authentication;

namespace Sangam.Identity.Server.Tests;

/// <summary>PR-10: production certificates load from files, and the server starts in Production.</summary>
[Collection("server")]
public sealed class ProductionStartTests : IDisposable
{
    private const string Password = "test-only";
    private readonly SangamServerFactory _factory;
    private readonly string _dir = Directory.CreateTempSubdirectory("sangam-certs").FullName;

    public ProductionStartTests(SangamServerFactory factory)
    {
        _factory = factory;
    }

    public void Dispose()
    {
        Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Load_ReadsCurrentAndPreviousCertificates_InOrder()
    {
        string current = Pfx("signing-current", withKey: true);
        string previous = Pfx("signing-previous", withKey: true);
        string encryption = Pfx("encryption", withKey: true);

        (IReadOnlyList<X509Certificate2> signing, IReadOnlyList<X509Certificate2> enc) = TokenCertificates.Load(Config(
            ("Sangam:Certificates:Signing:0:Path", current), ("Sangam:Certificates:Signing:0:Password", Password),
            ("Sangam:Certificates:Signing:1:Path", previous), ("Sangam:Certificates:Signing:1:Password", Password),
            ("Sangam:Certificates:Encryption:0:Path", encryption), ("Sangam:Certificates:Encryption:0:Password", Password)));

        Assert.Equal(["CN=signing-current", "CN=signing-previous"], signing.Select(c => c.Subject));
        Assert.Single(enc);
    }

    [Fact]
    public void Load_Refuses_WhenNothingIsConfigured_OrAFileIsMissing_OrTheKeyIsAbsent()
    {
        Assert.Contains("No production signing certificate", Assert.Throws<InvalidOperationException>(() => TokenCertificates.Load(Config())).Message, StringComparison.Ordinal);
        Assert.Contains("does not exist", Assert.Throws<InvalidOperationException>(() => TokenCertificates.Load(Config(("Sangam:Certificates:Signing:0:Path", Path.Combine(_dir, "nope.pfx"))))).Message, StringComparison.Ordinal);
        string keyless = Pfx("keyless", withKey: false);
        Assert.Contains("no private key", Assert.Throws<InvalidOperationException>(() => TokenCertificates.Load(Config(("Sangam:Certificates:Signing:0:Path", keyless), ("Sangam:Certificates:Signing:0:Password", Password)))).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheServer_StartsInProduction_WithCertificatesAndAnjal()
    {
        string signing = Pfx("prod-signing", withKey: true);
        string encryption = Pfx("prod-encryption", withKey: true);
        string keyRing = Pfx("prod-keyring", withKey: true);
        WebApplicationFactory<Program> production = _factory.WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Production");
            b.UseSetting("Sangam:Email:UseOutbox", "false");
            b.UseSetting("Sangam:Anjal:BaseUrl", "https://anjal.example.invalid/");
            b.UseSetting("Sangam:Anjal:ApiKey", "anjal-test-key-0123456789");
            b.UseSetting("Sangam:Certificates:Signing:0:Path", signing);
            b.UseSetting("Sangam:Certificates:Signing:0:Password", Password);
            b.UseSetting("Sangam:Certificates:Encryption:0:Path", encryption);
            b.UseSetting("Sangam:Certificates:Encryption:0:Password", Password);
            b.UseSetting("Sangam:DataProtection:PersistKeys", "true");
            b.UseSetting("Sangam:DataProtection:Certificates:0:Path", keyRing);
            b.UseSetting("Sangam:DataProtection:Certificates:0:Password", Password);
            b.UseSetting("Sangam:Sms:Enabled", "false");
        });
        using HttpClient client = production.CreateClient();

        using HttpResponseMessage response = await client.GetAsync(new Uri(WebHosting.LivePath, UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public void TheServer_RefusesProduction_WithTheDevelopmentSmsOutbox()
    {
        string signing = Pfx("sms-signing", withKey: true);
        string encryption = Pfx("sms-encryption", withKey: true);
        string keyRing = Pfx("sms-keyring", withKey: true);
        WebApplicationFactory<Program> production = _factory.WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Production");
            b.UseSetting("Sangam:Email:UseOutbox", "false");
            b.UseSetting("Sangam:Anjal:BaseUrl", "https://anjal.example.invalid/");
            b.UseSetting("Sangam:Anjal:ApiKey", "anjal-test-key-0123456789");
            b.UseSetting("Sangam:Certificates:Signing:0:Path", signing);
            b.UseSetting("Sangam:Certificates:Signing:0:Password", Password);
            b.UseSetting("Sangam:Certificates:Encryption:0:Path", encryption);
            b.UseSetting("Sangam:Certificates:Encryption:0:Password", Password);
            b.UseSetting("Sangam:DataProtection:PersistKeys", "true");
            b.UseSetting("Sangam:DataProtection:Certificates:0:Path", keyRing);
            b.UseSetting("Sangam:DataProtection:Certificates:0:Password", Password);
            b.UseSetting("Sangam:Sms:Enabled", "true");
            b.UseSetting("Sangam:Sms:Provider", "outbox");
        });

        InvalidOperationException refused = Assert.Throws<InvalidOperationException>(() => production.CreateClient().Dispose());
        Assert.Contains("development SMS outbox", refused.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("false", false, "not persisted")]
    [InlineData("true", false, "unencrypted")]
    public void KeyRing_RefusesProduction_WhenNotPersistedOrNotEncrypted(string persist, bool withCertificate, string expected)
    {
        List<(string, string)> settings = [("Sangam:DataProtection:PersistKeys", persist)];
        if (withCertificate)
        {
            settings.Add(("Sangam:DataProtection:Certificates:0:Path", Pfx("keyring", withKey: true)));
        }

        string? problem = KeyRingProtection.Validate("Production", Config([.. settings]));

        Assert.NotNull(problem);
        Assert.Contains(expected, problem, StringComparison.Ordinal);
        Assert.Null(KeyRingProtection.Validate("Development", Config([.. settings])));
    }

    private static IConfiguration Config(params (string Key, string Value)[] values)
    {
        return new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value))).Build();
    }

    private string Pfx(string name, bool withKey)
    {
        using RSA rsa = RSA.Create(2048);
        CertificateRequest request = new($"CN={name}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using X509Certificate2 certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        string path = Path.Combine(_dir, name + ".pfx");
        byte[] bytes = withKey
            ? certificate.Export(X509ContentType.Pkcs12, Password)
            : X509CertificateLoader.LoadCertificate(certificate.Export(X509ContentType.Cert)).Export(X509ContentType.Pkcs12, Password);
        File.WriteAllBytes(path, bytes);
        return path;
    }
}
