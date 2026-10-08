using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Sangam.Identity.Application.Portal;
using Sangam.Identity.Application.Verification;
using Sangam.Identity.Domain;
using Sangam.Identity.Domain.Entities;
using Sangam.Identity.Domain.Enums;
using Sangam.Identity.Infrastructure.Maintenance;
using Sangam.Identity.Infrastructure.Persistence;
using Sangam.Identity.Infrastructure.Tests.Accounts;
using Sangam.Identity.Infrastructure.Tests.Postgres;
using Sangam.Identity.Infrastructure.Verification;

namespace Sangam.Identity.Infrastructure.Tests.Verification;

/// <summary>
/// PR-26 (SGM-201, SGM-204): DigiLocker verification — the record's name, date of birth and gender replace the profile's and are
/// locked; one DigiLocker identity verifies one account; the DigiLocker id is kept only as a keyed hash; removing the
/// verification unlocks the profile; deleting the account deletes the verification.
/// </summary>
[Collection("postgres")]
public sealed class IdentityVerificationTests : IAsyncLifetime
{
    private readonly PostgresFixture _pg;
    private ServiceProvider _provider = null!;
    private Guid _ravi;
    private Guid _meera;

    public IdentityVerificationTests(PostgresFixture pg)
    {
        _pg = pg;
    }

    public async Task InitializeAsync()
    {
        if (!_pg.IsAvailable)
        {
            return;
        }

        await _pg.ResetAsync();
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Sangam"] = _pg.ConnectionString,
            ["Sangam:Email:UseOutbox"] = "true",
            ["Sangam:Maintenance:Enabled"] = "false",
            ["Sangam:DataProtection:PersistKeys"] = "false",
            ["Sangam:DigiLocker:SubjectKey"] = "a-test-key-for-digilocker-ids-0123456789",
        }).Build();
        ServiceCollection services = new();
        services.AddLogging();
        services.AddDataProtection();
        services.AddSingleton(configuration);
        services.AddSangamInfrastructure(configuration);
        _provider = services.BuildServiceProvider();

        await using SangamDbContext db = _pg.CreateContext();
        SangamUser ravi = TestUsers.New("ravi@verify.test", "+919876505001");
        SangamUser meera = TestUsers.New("meera@verify.test", "+919876505002");
        db.Users.AddRange(ravi, meera);
        await db.SaveChangesAsync();
        (_ravi, _meera) = (ravi.Id, meera.Id);
    }

    public async Task DisposeAsync()
    {
        if (_provider is not null)
        {
            await _provider.DisposeAsync();
        }
    }

    [Theory]
    [InlineData("{\"digilockerid\":\"dl-1\",\"name\":\"Ravi Kumar\",\"dob\":\"12061986\",\"gender\":\"M\"}", "Ravi Kumar", Gender.Male)]
    [InlineData("{\"digilockerid\":\"dl-1\",\"name\":\" Meera \",\"dob\":\"01011990\",\"gender\":\"f\"}", "Meera", Gender.Female)]
    [InlineData("{\"digilockerid\":\"dl-1\",\"name\":\"Kala\",\"dob\":\"01011990\",\"gender\":\"T\"}", "Kala", Gender.Other)]
    public void Parse_ReadsDigiLockersFields(string json, string name, Gender gender)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        VerifiedIdentity identity = DigiLockerClient.Parse(document.RootElement)!;
        Assert.Equal(("digilocker", "dl-1", name, gender), (identity.Method, identity.Subject, identity.Name, identity.Gender));
    }

    [Theory]
    [InlineData("{\"name\":\"Ravi\",\"dob\":\"12061986\",\"gender\":\"M\"}")]
    [InlineData("{\"digilockerid\":\"dl-1\",\"dob\":\"12061986\",\"gender\":\"M\"}")]
    [InlineData("{\"digilockerid\":\"dl-1\",\"name\":\"Ravi\",\"dob\":\"1986-06-12\",\"gender\":\"M\"}")]
    [InlineData("{\"digilockerid\":\"dl-1\",\"name\":\"Ravi\",\"dob\":\"31021986\",\"gender\":\"M\"}")]
    [InlineData("{\"digilockerid\":\"dl-1\",\"name\":\"Ravi\",\"dob\":\"12061986\",\"gender\":\"X\"}")]
    public void Parse_RefusesAnythingIncomplete(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        Assert.Null(DigiLockerClient.Parse(document.RootElement));
    }

    [Theory]
    [InlineData("RAVI KUMAR MENON", "RAVI KUMAR", "MENON")]
    [InlineData("Meera  Iyer", "Meera", "Iyer")]
    [InlineData("Kala", "Kala", "")]
    public void SplitName_KeepsTheRecordsWords_AndTakesTheLastAsTheFamilyName(string name, string first, string last)
    {
        Assert.Equal((first, last), EfIdentityVerificationService.SplitName(name));
    }

    [Fact]
    public void ThePkceChallenge_IsTheS256OfTheVerifier()
    {
        (string verifier, string challenge) = DigiLockerClient.NewPkce();
        Assert.Equal(43, verifier.Length);
        string expected = Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(verifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        Assert.Equal(expected, challenge);
    }

    [Fact]
    public void TheGuard_AsksForCredentialsHttpsAndARealKey_OutsideDevelopment()
    {
        Dictionary<string, string?> settings = new()
        {
            ["Sangam:DigiLocker:Enabled"] = "true",
            ["Sangam:DigiLocker:ClientId"] = "id",
            ["Sangam:DigiLocker:ClientSecret"] = "secret",
            ["Sangam:DigiLocker:SubjectKey"] = new string('k', 32),
        };
        Assert.Null(DigiLockerGuard.Validate("Production", Build(settings)));
        Assert.Null(DigiLockerGuard.Validate("Development", Build(new() { ["Sangam:DigiLocker:Enabled"] = "true" })));
        Assert.NotNull(DigiLockerGuard.Validate("Production", Build(new(settings) { ["Sangam:DigiLocker:ClientSecret"] = "" })));
        Assert.NotNull(DigiLockerGuard.Validate("Production", Build(new(settings) { ["Sangam:DigiLocker:SubjectKey"] = "short" })));
        Assert.NotNull(DigiLockerGuard.Validate("Production", Build(new(settings) { ["Sangam:DigiLocker:TokenUrl"] = "http://api.digitallocker.gov.in/token" })));
        Assert.Null(DigiLockerGuard.Validate("Production", Build(new() { ["Sangam:DigiLocker:Enabled"] = "false" })));

        static IConfiguration Build(Dictionary<string, string?> values) => new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    [PostgresFact]
    public async Task Verifying_TakesTheRecordsValues_LocksThem_AndOneIdentityVerifiesOneAccount()
    {
        using IServiceScope scope = _provider.CreateScope();
        IIdentityVerificationService service = scope.ServiceProvider.GetRequiredService<IIdentityVerificationService>();
        IPortalService portal = scope.ServiceProvider.GetRequiredService<IPortalService>();
        VerifiedIdentity record = new("digilocker", "dl-ravi-0001", "RAVI KUMAR MENON", new DateOnly(1986, 6, 14), Gender.Male);

        Assert.False((await service.GetAsync(_ravi)).Verified);
        Assert.Equal(new VerificationResult(true, "verified"), await service.ApplyAsync(_ravi, record, "203.0.113.5"));

        await using (SangamDbContext db = _pg.CreateContext())
        {
            SangamUser user = await db.Users.SingleAsync(u => u.Id == _ravi);
            Assert.Equal(("RAVI KUMAR", "MENON", new DateOnly(1986, 6, 14), Gender.Male), (user.FirstName, user.LastName, user.DateOfBirth, user.Gender));
            Assert.NotNull(user.IdentityVerifiedAt);
            IdentityVerification row = await db.IdentityVerifications.SingleAsync(v => v.UserId == _ravi);
            Assert.Equal(64, row.SubjectHash.Length);
            Assert.DoesNotContain("dl-ravi", row.SubjectHash, StringComparison.Ordinal);
            Assert.True(await db.AuditEvents.AnyAsync(e => e.Action == AuditActions.IdentityVerify && e.TargetId == _ravi));
        }

        // Locked: the name and gender cannot change; everything else can.
        Assert.False((await portal.UpdateProfileAsync(_ravi, new ProfileUpdate("Ravi", "Menon", "en-IN", Gender.Male, "IN", "9876505001"), null)).Succeeded);
        Assert.False((await portal.UpdateProfileAsync(_ravi, new ProfileUpdate("RAVI KUMAR", "MENON", "en-IN", Gender.Other, "IN", "9876505001"), null)).Succeeded);
        Assert.True((await portal.UpdateProfileAsync(_ravi, new ProfileUpdate("RAVI KUMAR", "MENON", "ml-IN", Gender.Male, "IN", "9876505001"), null)).Succeeded);

        // The same DigiLocker identity cannot verify a second account; verifying again on the first is fine.
        Assert.Equal(new VerificationResult(false, "taken"), await service.ApplyAsync(_meera, record, null));
        Assert.Equal(new VerificationResult(true, "verified"), await service.ApplyAsync(_ravi, record, null));

        // Removing unlocks, and frees the identity.
        Assert.Equal(new VerificationResult(true, "removed"), await service.RemoveAsync(_ravi, null));
        Assert.Equal(new VerificationResult(false, "none"), await service.RemoveAsync(_ravi, null));
        Assert.True((await portal.UpdateProfileAsync(_ravi, new ProfileUpdate("Ravi", "Menon", "ml-IN", Gender.Male, "IN", "9876505001"), null)).Succeeded);
        Assert.Equal(new VerificationResult(true, "verified"), await service.ApplyAsync(_meera, record with { Name = "Meera" }, null));

        await using SangamDbContext check = _pg.CreateContext();
        Assert.True(await check.AuditEvents.AnyAsync(e => e.Action == AuditActions.IdentityVerifyRefused && e.TargetId == _meera));
        Assert.True(await check.AuditEvents.AnyAsync(e => e.Action == AuditActions.IdentityUnverify && e.TargetId == _ravi));
        Assert.Equal(string.Empty, (await check.Users.SingleAsync(u => u.Id == _meera)).LastName);
    }

    [PostgresFact]
    public async Task DeletingTheAccount_DeletesTheVerification_SoThePersonCanVerifyANewOne()
    {
        using IServiceScope scope = _provider.CreateScope();
        IIdentityVerificationService service = scope.ServiceProvider.GetRequiredService<IIdentityVerificationService>();
        VerifiedIdentity record = new("digilocker", "dl-ravi-0002", "Ravi Menon", new DateOnly(1986, 6, 12), Gender.Male);
        Assert.True((await service.ApplyAsync(_ravi, record, null)).Succeeded);

        await using (SangamDbContext db = _pg.CreateContext())
        {
            await db.Users.Where(u => u.Id == _ravi).ExecuteUpdateAsync(u => u.SetProperty(x => x.PurgeAfter, DateTimeOffset.UtcNow.AddMinutes(-1)));
        }

        AccountPurgeService purge = _provider.GetServices<Microsoft.Extensions.Hosting.IHostedService>().OfType<AccountPurgeService>().Single();
        Assert.Equal(1, (await purge.RunOnceAsync()).Accounts);

        await using SangamDbContext after = _pg.CreateContext();
        Assert.False(await after.IdentityVerifications.AnyAsync());
        Assert.Null((await after.Users.SingleAsync(u => u.Id == _ravi)).IdentityVerifiedAt);
        Assert.True((await service.ApplyAsync(_meera, record, null)).Succeeded);
    }
}
