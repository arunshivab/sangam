using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Sangam.Identity.Infrastructure.Persistence;
using Sangam.Identity.Infrastructure.Tests.Postgres;

namespace Sangam.Identity.Infrastructure.Tests.Persistence;

/// <summary>V-01: the key ring is stored encrypted with a certificate, and survives a rotation.</summary>
[Collection("postgres")]
public sealed class KeyRingEncryptionTests : IAsyncLifetime, IDisposable
{
    private const string Password = "test-only";
    private readonly PostgresFixture _pg;
    private readonly string _dir = Directory.CreateTempSubdirectory("sangam-keyring").FullName;

    public KeyRingEncryptionTests(PostgresFixture pg)
    {
        _pg = pg;
    }

    public Task InitializeAsync() => _pg.IsAvailable ? _pg.ResetAsync() : Task.CompletedTask;

    public Task DisposeAsync() => Task.CompletedTask;

    public void Dispose()
    {
        Directory.Delete(_dir, recursive: true);
    }

    [PostgresFact]
    public async Task Keys_AreStoredEncrypted_AndRemainReadableAfterTheCertificateIsRotated()
    {
        string first = Pfx("keyring-1");
        string second = Pfx("keyring-2");
        string secret;
        using (ServiceProvider host = Host(first))
        {
            secret = host.GetRequiredService<IDataProtectionProvider>().CreateProtector("test").Protect("payload");
        }

        await using (SangamDbContext db = _pg.CreateContext())
        {
            string xml = await db.DataProtectionKeys.Select(k => k.Xml!).FirstAsync();
            Assert.Contains("EncryptedData", xml, StringComparison.Ordinal);
            Assert.DoesNotContain("<masterKey", xml, StringComparison.Ordinal);
        }

        // After rotation the new certificate is listed first and the old one stays listed.
        using ServiceProvider rotated = Host(second, first);
        Assert.Equal("payload", rotated.GetRequiredService<IDataProtectionProvider>().CreateProtector("test").Unprotect(secret));
    }

    private ServiceProvider Host(params string[] certificates)
    {
        Dictionary<string, string?> settings = new()
        {
            ["ConnectionStrings:Sangam"] = _pg.ConnectionString,
            ["Sangam:Email:UseOutbox"] = "true",
            ["Sangam:Maintenance:Enabled"] = "false",
        };
        for (int i = 0; i < certificates.Length; i++)
        {
            settings[$"Sangam:DataProtection:Certificates:{i}:Path"] = certificates[i];
            settings[$"Sangam:DataProtection:Certificates:{i}:Password"] = Password;
        }

        ServiceCollection services = new();
        services.AddLogging();
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        services.AddSingleton(configuration);
        services.AddSangamInfrastructure(configuration);
        return services.BuildServiceProvider();
    }

    private string Pfx(string name)
    {
        using RSA rsa = RSA.Create(2048);
        CertificateRequest request = new($"CN={name}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using X509Certificate2 certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        string path = Path.Combine(_dir, name + ".pfx");
        File.WriteAllBytes(path, certificate.Export(X509ContentType.Pkcs12, Password));
        return path;
    }
}
