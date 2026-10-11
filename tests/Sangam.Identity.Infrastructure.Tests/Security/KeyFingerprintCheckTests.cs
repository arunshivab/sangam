using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Sangam.Identity.Domain;
using Sangam.Identity.Domain.Entities;
using Sangam.Identity.Infrastructure.Persistence;
using Sangam.Identity.Infrastructure.Security;
using Sangam.Identity.Infrastructure.Tests.Postgres;

namespace Sangam.Identity.Infrastructure.Tests.Security;

/// <summary>
/// rc.8 (SGM-911, A6 workaround): a key that changed since the last start stops the start and is audited; a planned change
/// is accepted for one start; a new key is recorded; nothing about a secret can be read back from its fingerprint.
/// </summary>
[Collection("postgres")]
public sealed class KeyFingerprintCheckTests : IAsyncLifetime
{
    private static readonly string[] BothKeys = ["cert.signing.0", "secret.pepper"];
    private static readonly string[] SigningOnly = ["cert.signing.0"];
    private readonly PostgresFixture _pg;
    private readonly string _dir = Directory.CreateTempSubdirectory("sangam-keys-").FullName;
    private ServiceProvider _provider = null!;

    public KeyFingerprintCheckTests(PostgresFixture pg)
    {
        _pg = pg;
    }

    private string Signing => Path.Combine(_dir, "signing_current.pfx");

    public async Task InitializeAsync()
    {
        if (!_pg.IsAvailable)
        {
            return;
        }

        await _pg.ResetAsync();
        await using SangamDbContext db = _pg.CreateContext();
        await db.HostReports.Where(r => r.Host == KeyFingerprintCheck.ReportHost).ExecuteDeleteAsync();
        await File.WriteAllBytesAsync(Signing, [1, 2, 3, 4]);

        ServiceCollection services = new();
        services.AddLogging();
        services.AddDataProtection();
        services.AddSingleton<IHostEnvironment>(new TestEnvironment());
        IConfiguration configuration = Configuration(accept: false, pepper: "first-pepper-of-at-least-32-characters!!");
        services.AddSingleton(configuration);
        services.AddSangamInfrastructure(configuration);
        _provider = services.BuildServiceProvider();
    }

    public async Task DisposeAsync()
    {
        if (_provider is not null)
        {
            await _provider.DisposeAsync();
        }

        Directory.Delete(_dir, recursive: true);
    }

    [PostgresFact]
    public async Task AChangedKey_StopsTheStart_UntilTheChangeIsAccepted()
    {
        // First start: both keys are recorded, and the start goes on.
        Assert.Empty(await Check(accept: false, pepper: "first-pepper-of-at-least-32-characters!!").CheckAsync());
        Assert.Empty(await Check(accept: false, pepper: "first-pepper-of-at-least-32-characters!!").CheckAsync());
        await using (SangamDbContext db = _pg.CreateContext())
        {
            List<string> names = await db.HostReports.Where(r => r.Host == KeyFingerprintCheck.ReportHost).Select(r => r.Subject).OrderBy(s => s).ToListAsync();
            Assert.Equal(BothKeys, names);
            Assert.Single(await db.AuditEvents.Where(e => e.Action == AuditActions.SecurityKeyRecorded).ToListAsync());
        }

        // The certificate file is swapped: the start is refused, nothing is recorded, and the refusal is audited.
        await File.WriteAllBytesAsync(Signing, [9, 9, 9]);
        Assert.Equal(SigningOnly, await Check(accept: false, pepper: "first-pepper-of-at-least-32-characters!!").CheckAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => Check(accept: false, pepper: "first-pepper-of-at-least-32-characters!!").StartingAsync(CancellationToken.None));
        await using (SangamDbContext db = _pg.CreateContext())
        {
            AuditEvent refused = await db.AuditEvents.AsNoTracking().OrderByDescending(e => e.Id).FirstAsync(e => e.Action == AuditActions.SecurityKeyMismatch);
            using JsonDocument detail = JsonDocument.Parse(refused.Metadata);
            Assert.False(detail.RootElement.GetProperty("accepted").GetBoolean());
            Assert.Equal("cert.signing.0", detail.RootElement.GetProperty("keys")[0].GetString());
        }

        // A changed pepper is caught the same way.
        Assert.Equal(BothKeys, await Check(accept: false, pepper: "another-pepper-of-at-least-32-characters").CheckAsync());

        // Accepted once: the new fingerprints are recorded, and the next ordinary start goes on.
        Assert.Empty(await Check(accept: true, pepper: "another-pepper-of-at-least-32-characters").CheckAsync());
        Assert.Empty(await Check(accept: false, pepper: "another-pepper-of-at-least-32-characters").CheckAsync());
    }

    [Fact]
    public void ASecretsFingerprint_IsKeyed_AndDoesNotContainTheSecret()
    {
        string fingerprint = KeyFingerprintCheck.SecretFingerprint("first-pepper-of-at-least-32-characters!!");
        Assert.Equal(32, fingerprint.Length);
        Assert.NotEqual(fingerprint, KeyFingerprintCheck.SecretFingerprint("first-pepper-of-at-least-32-characters!?"));
        Assert.Equal(fingerprint, KeyFingerprintCheck.SecretFingerprint("first-pepper-of-at-least-32-characters!!"));
    }

    private KeyFingerprintCheck Check(bool accept, string pepper)
        => new(_provider.GetRequiredService<IServiceScopeFactory>(), Configuration(accept, pepper), new TestEnvironment { EnvironmentName = "Production" }, NullLogger<KeyFingerprintCheck>.Instance);

    private IConfiguration Configuration(bool accept, string pepper) => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["ConnectionStrings:Sangam"] = _pg.ConnectionString,
        ["Sangam:Email:UseOutbox"] = "true",
        ["Sangam:PasswordHashing:MemoryKiB"] = "8192",
        ["Sangam:PasswordHashing:Iterations"] = "2",
        ["Sangam:PasswordHashing:Pepper"] = pepper,
        ["Sangam:Maintenance:Enabled"] = "false",
        ["Sangam:Certificates:Signing:0:Path"] = Signing,
        [KeyFingerprintCheck.AcceptChangedKey] = accept ? "true" : "false",
    }).Build();

    private sealed class TestEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";

        public string ApplicationName { get; set; } = "Sangam.Tests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
