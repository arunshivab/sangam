using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Sangam.Identity.Application.Abstractions;
using Sangam.Identity.Domain;
using Sangam.Identity.Domain.Entities;
using Sangam.Identity.Domain.Enums;
using Sangam.Identity.Infrastructure.Audit;
using Sangam.Identity.Infrastructure.Persistence;
using Sangam.Identity.Infrastructure.Services;
using Sangam.Identity.Infrastructure.Tests.Postgres;

namespace Sangam.Identity.Infrastructure.Tests.Persistence;

/// <summary>
/// D-A: audit events stay live for a year, then move to an encrypted archive with IP addresses truncated and user
/// agents dropped, readable only with the founder's offline key, and are purged at seven years. Nothing is removed
/// until the archive is set up.
/// </summary>
[Collection("postgres")]
public sealed class AuditArchiveTests : IAsyncLifetime
{
    private const string Password = "founder-offline-passphrase";
    private readonly PostgresFixture _pg;
    private readonly string _dir = Directory.CreateTempSubdirectory("sangam-audit-").FullName;
    private readonly List<ServiceProvider> _providers = [];
    private string _certificate = string.Empty;
    private string _key = string.Empty;

    public AuditArchiveTests(PostgresFixture pg)
    {
        _pg = pg;
    }

    public async Task InitializeAsync()
    {
        (_certificate, _key) = AuditArchiveCommand.KeyGen(Path.Combine(_dir, "keys"), Password);
        if (_pg.IsAvailable)
        {
            await _pg.ResetAsync();
        }
    }

    public async Task DisposeAsync()
    {
        foreach (ServiceProvider provider in _providers)
        {
            await provider.DisposeAsync();
        }

        Directory.Delete(_dir, recursive: true);
    }

    [PostgresFact]
    public async Task WithoutTheArchive_NothingIsRemoved()
    {
        await SeedAsync(DateTimeOffset.UtcNow.AddDays(-400), 2);
        AuditArchiveRun run = await Archiver(archive: false).RunOnceAsync();

        Assert.Equal(new AuditArchiveRun(0, 0, 0), run);
        await using SangamDbContext db = _pg.CreateContext();
        Assert.Equal(2, await db.AuditEvents.CountAsync());
    }

    [PostgresFact]
    public async Task AYearOld_EventsMoveToTheEncryptedArchive_Anonymised_AndTheChainStillVerifies()
    {
        await SeedAsync(DateTimeOffset.UtcNow.AddDays(-400), 3);
        await SeedAsync(DateTimeOffset.UtcNow.AddDays(-10), 1);

        AuditArchiveRun run = await Archiver(archive: true).RunOnceAsync();
        Assert.Equal((1, 3, 0), (run.Files, run.Events, run.Purged));

        await using SangamDbContext db = _pg.CreateContext();
        List<AuditEvent> live = await db.AuditEvents.AsNoTracking().OrderBy(e => e.Id).ToListAsync();
        Assert.Equal([AuditActions.UserLoginSuccess, AuditActions.AuditArchive], live.Select(e => e.Action));
        Assert.Null(await AuditChain.VerifyAsync(db));

        string file = Assert.Single(Directory.GetFiles(Archives(), "*" + AuditArchiveFile.Extension));
        using JsonDocument record = JsonDocument.Parse(live[1].Metadata);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(file))), record.RootElement.GetProperty("sha256").GetString());

        // Only the founder's offline key reads it; the IP addresses are truncated and the user agents gone.
        string text = await File.ReadAllTextAsync(file);
        Assert.DoesNotContain("203.0.113.77", text, StringComparison.Ordinal);
        using RSA key = RSA.Create();
        key.ImportFromEncryptedPem(await File.ReadAllTextAsync(_key), Password);
        (AuditArchiveHeader header, IReadOnlyList<ArchivedAuditEvent> events) = AuditArchiveFile.Read(file, key);
        Assert.Null(AuditArchiveCommand.Check(header, events));
        Assert.Equal(3, events.Count);
        Assert.All(events, e => Assert.Equal("203.0.113.0", e.IpAddress));
        Assert.All(events, e => Assert.DoesNotContain("Mozilla", e.Metadata, StringComparison.Ordinal));
        Assert.All(events, e => Assert.Contains("\"ip\":\"2001:db8:85a3::\"", e.Metadata, StringComparison.Ordinal));
        Assert.Equal(header.LastHash, live[0].PrevHash);

        // The tool lists without a key, and reads with one.
        using StringWriter output = new();
        Assert.Equal(0, await AuditArchiveCommand.RunAsync(["list", "--dir", Archives()], output));
        Assert.Contains("3 events", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(0, await AuditArchiveCommand.RunAsync(["read", "--file", file, "--key", _key, "--password", Password, "--out", Path.Combine(_dir, "out.jsonl")], output));
        Assert.Equal(3, (await File.ReadAllLinesAsync(Path.Combine(_dir, "out.jsonl"))).Length);
        Assert.Equal(1, await AuditArchiveCommand.RunAsync(["read", "--file", file, "--key", _key, "--password", "wrong-password-entirely"], output));
    }

    [PostgresFact]
    public async Task AnArchive_IsPurgedAtSevenYears_AndThePurgeIsAudited()
    {
        await SeedAsync(DateTimeOffset.UtcNow.AddYears(-8), 2);
        AuditArchiveRun first = await Archiver(archive: true).RunOnceAsync();
        Assert.Equal((1, 2, 1), (first.Files, first.Events, first.Purged));
        Assert.Empty(Directory.GetFiles(Archives(), "*" + AuditArchiveFile.Extension));

        await SeedAsync(DateTimeOffset.UtcNow.AddYears(-6), 2);
        AuditArchiveRun second = await Archiver(archive: true).RunOnceAsync();
        Assert.Equal((1, 2, 0), (second.Files, second.Events, second.Purged));
        Assert.Single(Directory.GetFiles(Archives(), "*" + AuditArchiveFile.Extension));

        await using SangamDbContext db = _pg.CreateContext();
        Assert.Equal(1, await db.AuditEvents.CountAsync(e => e.Action == AuditActions.AuditArchivePurge));
        Assert.Equal(2, await db.AuditEvents.CountAsync(e => e.Action == AuditActions.AuditArchive));
        Assert.Null(await AuditChain.VerifyAsync(db));
        Assert.Contains("\"Files\":1", (await db.HostReports.SingleAsync(r => r.Subject == AuditArchiver.ReportSubject)).Payload, StringComparison.Ordinal);
    }

    [PostgresFact]
    public async Task CuttingOffTheHeadOfTheLog_OutsideTheArchive_IsDetected()
    {
        await SeedAsync(DateTimeOffset.UtcNow.AddDays(-5), 3);
        await using SangamDbContext db = _pg.CreateContext();
        Assert.Null(await AuditChain.VerifyAsync(db));
        long first = await db.AuditEvents.MinAsync(e => e.Id);
        await using (Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction tx = await db.Database.BeginTransactionAsync())
        {
            await db.Database.ExecuteSqlRawAsync("SELECT set_config('sangam.audit_maintenance', 'on', true);");
            await db.AuditEvents.Where(e => e.Id == first).ExecuteDeleteAsync();
            await tx.CommitAsync();
        }

        Assert.Equal(first + 1, await AuditChain.VerifyAsync(db));
    }

    [Fact]
    public void TheStart_IsRefused_ForTheOldDeleteOnlySetting_AHalfSetUpArchive_OrAPrivateKeyOnTheServer()
    {
        Assert.Contains("no longer used", Validate(("Sangam:Audit:RetentionDays", "365")), StringComparison.Ordinal);
        Assert.Contains("both", Validate(("Sangam:Audit:Directory", _dir)), StringComparison.Ordinal);
        Assert.Null(Validate(("Sangam:Audit:Directory", _dir), ("Sangam:Audit:CertificatePath", _certificate)));
        Assert.Null(Validate());

        string pfx = Path.Combine(_dir, "with-key.pfx");
        using (RSA rsa = RSA.Create(3072))
        {
            using System.Security.Cryptography.X509Certificates.X509Certificate2 own = new System.Security.Cryptography.X509Certificates.CertificateRequest("CN=x", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1).CreateSelfSigned(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddYears(1));
            File.WriteAllBytes(pfx, own.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Pfx));
        }

        Assert.Contains("cannot be used", Validate(("Sangam:Audit:Directory", _dir), ("Sangam:Audit:CertificatePath", pfx)), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("203.0.113.77", "203.0.113.0")]
    [InlineData("::ffff:198.51.100.9", "198.51.100.0")]
    [InlineData("2001:db8:85a3:8d3:1319:8a2e:370:7348", "2001:db8:85a3::")]
    [InlineData("not an address", null)]
    [InlineData(null, null)]
    public void IpAddresses_AreTruncated(string? ip, string? expected)
    {
        Assert.Equal(expected, AuditArchiveFile.TruncateIp(ip));
    }

    private string Archives() => Path.Combine(_dir, "archive");

    private static string? Validate(params (string Key, string Value)[] settings)
        => AuditArchiveOptions.Validate("Production", new ConfigurationBuilder().AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value))).Build());

    private AuditArchiver Archiver(bool archive)
    {
        Dictionary<string, string?> settings = new()
        {
            ["ConnectionStrings:Sangam"] = _pg.ConnectionString,
            ["Sangam:Email:UseOutbox"] = "true",
            ["Sangam:Maintenance:Enabled"] = "false",
            ["Sangam:DataProtection:PersistKeys"] = "false",
        };
        if (archive)
        {
            settings["Sangam:Audit:Directory"] = Archives();
            settings["Sangam:Audit:CertificatePath"] = _certificate;
        }

        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        ServiceCollection services = new();
        services.AddLogging();
        services.AddDataProtection();
        services.AddSingleton(configuration);
        services.AddSangamInfrastructure(configuration);
        ServiceProvider provider = services.BuildServiceProvider();
        _providers.Add(provider);
        return provider.GetRequiredService<AuditArchiver>();
    }

    private async Task SeedAsync(DateTimeOffset at, int count)
    {
        EfAuditWriter writer = new(new FixtureContextFactory(_pg), new FixedClock(at));
        for (int i = 0; i < count; i++)
        {
            await writer.WriteAsync(new AuditEntry(
                at > DateTimeOffset.UtcNow.AddDays(-30) ? AuditActions.UserLoginSuccess : AuditActions.UserRegister,
                AuditActorType.User,
                Guid.NewGuid(),
                Metadata: "{\"ip\":\"2001:db8:85a3:8d3:1319:8a2e:370:7348\",\"user_agent\":\"Mozilla/5.0\",\"method\":\"password\"}",
                IpAddress: "203.0.113.77",
                UserAgent: "Mozilla/5.0 (X11; Linux x86_64)"));
        }
    }

    private sealed class FixedClock : IClock
    {
        public FixedClock(DateTimeOffset now)
        {
            UtcNow = now;
        }

        public DateTimeOffset UtcNow { get; }
    }

    private sealed class FixtureContextFactory : IDbContextFactory<SangamDbContext>
    {
        private readonly PostgresFixture _pg;

        public FixtureContextFactory(PostgresFixture pg)
        {
            _pg = pg;
        }

        public SangamDbContext CreateDbContext() => _pg.CreateContext();
    }
}
