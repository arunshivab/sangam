using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Sangam.Client.Audit;
using Sangam.Identity.Application.Accounts;
using Sangam.Identity.Application.Admin;
using Sangam.Identity.Application.Evidence;
using Sangam.Identity.Application.Partners;
using Sangam.Identity.Application.Tenancy;
using Sangam.Identity.Domain;
using Sangam.Identity.Domain.Entities;
using Sangam.Identity.Domain.Enums;
using Sangam.Identity.Infrastructure.Evidence;
using Sangam.Identity.Infrastructure.Persistence;
using Sangam.Identity.Infrastructure.Services;
using Sangam.Identity.Infrastructure.Tests.Postgres;

namespace Sangam.Identity.Infrastructure.Tests.Partners;

/// <summary>
/// PR-32 (CAP-110): an application's evidence pack — for its administrators only, every file hashed in the manifest,
/// the access list and the joiners and leavers read from Sangam's records, the audit trail in the shared schema, no
/// secret, no spreadsheet formula, and the download itself audited.
/// </summary>
[Collection("postgres")]
public sealed class EvidencePackTests : IAsyncLifetime
{
    private readonly PostgresFixture _pg;
    private ServiceProvider _provider = null!;
    private Guid _app;
    private Guid _owner;
    private Guid _nurse;
    private Guid _leaver;
    private Guid _stranger;
    private Guid _hospital;

    public EvidencePackTests(PostgresFixture pg)
    {
        _pg = pg;
    }

    private static DateOnly Today => DateOnly.FromDateTime(DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(5.5)).DateTime);

    public async Task InitializeAsync()
    {
        if (!_pg.IsAvailable)
        {
            return;
        }

        await _pg.ResetAsync();
        ServiceCollection services = new();
        services.AddLogging();
        services.AddDataProtection();
        services.AddSingleton<IHostEnvironment>(new TestEnvironment());
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Sangam"] = _pg.ConnectionString,
            ["Sangam:Email:UseOutbox"] = "true",
            ["Sangam:PasswordHashing:MemoryKiB"] = "8192",
            ["Sangam:PasswordHashing:Iterations"] = "2",
            ["Sangam:Otp:ResendCooldown"] = "00:00:00",
            ["Sangam:Maintenance:Enabled"] = "false",
        }).Build();
        services.AddSingleton(configuration);
        services.AddSangamInfrastructure(configuration);
        _provider = services.BuildServiceProvider();

        _owner = await NewUserAsync("owner@lims.test", "+919876502001");
        _nurse = await NewUserAsync("nurse@lims.test", "+919876502002");
        _leaver = await NewUserAsync("leaver@lims.test", "+919876502003");
        _stranger = await NewUserAsync("stranger@lims.test", "+919876502004");

        DateTimeOffset now = DateTimeOffset.UtcNow;
        await using (SangamDbContext db = _pg.CreateContext())
        {
            App app = new() { Id = Guid.NewGuid(), ClientId = "lims", Slug = "lims", DisplayName = "City LIMS", OwnerCompanyName = "Lab Co", CreatedAt = now, UpdatedAt = now };
            db.Apps.Add(app);
            foreach (Guid linked in new[] { _owner, _nurse, _leaver })
            {
                db.AppGrants.Add(new AppGrant { Id = Guid.NewGuid(), AppId = app.Id, UserId = linked, GrantedAt = now });
            }

            db.AppAdmins.Add(new AppAdmin { Id = Guid.NewGuid(), AppId = app.Id, UserId = _owner, Role = AppAdminRole.Owner, GrantedAt = now });
            db.ScimTargets.Add(new ScimTarget { AppId = app.Id, BaseUrl = "https://lims.example.in/scim/v2", AuthMode = "bearer", ProtectedToken = "PROTECTED-SECRET-MUST-NOT-LEAK", GroupMapping = "role", Enabled = true, Status = "ok", CreatedAt = now, UpdatedAt = now });

            // A name typed to run as a spreadsheet formula.
            SangamUser nurse = await db.Users.SingleAsync(u => u.Id == _nurse);
            nurse.FirstName = "=HYPERLINK(\"https://evil.example\",\"x\")";
            await db.SaveChangesAsync();
            _app = app.Id;
        }

        using IServiceScope scope = _provider.CreateScope();
        IPartnerService partners = scope.ServiceProvider.GetRequiredService<IPartnerService>();
        _hospital = Guid.NewGuid();
        Assert.True((await partners.UpsertOrganisationAsync(_owner, _app, _hospital, new OrganisationUpsert("City Hospital", "hospital", null, null))).Succeeded);
        Assert.True((await partners.UpsertRoleAsync(_owner, _app, "technician", new RoleUpsert("Technician", null, ["results:read"], null))).Succeeded);
        Assert.True((await partners.GrantMembershipAsync(_owner, _app, _hospital, _nurse, new MembershipUpsert("technician", false))).Succeeded);
        Assert.True((await partners.GrantMembershipAsync(_owner, _app, _hospital, _leaver, new MembershipUpsert("technician", false))).Succeeded);
        Assert.True((await partners.RevokeMembershipAsync(_owner, _app, _hospital, _leaver)).Succeeded);
    }

    public async Task DisposeAsync()
    {
        if (_provider is not null)
        {
            await _provider.DisposeAsync();
        }
    }

    [PostgresFact]
    public async Task OnlyTheApplicationsAdministrators_GetAPack()
    {
        Assert.Null(await BuildAsync(_stranger));
        Assert.Null(await BuildAsync(_nurse));
        Assert.NotNull(await BuildAsync(_owner));
    }

    [PostgresFact]
    public async Task EveryFile_IsInTheManifest_WithItsSha256()
    {
        EvidencePack pack = (await BuildAsync(_owner))!;
        Dictionary<string, byte[]> files = Unzip(pack.Content);

        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(pack.Content)), pack.Sha256);
        JsonNode manifest = JsonNode.Parse(files["manifest.json"])!;
        Assert.Equal(EfEvidencePackService.Format, manifest["format"]!.GetValue<string>());
        JsonArray listed = manifest["files"]!.AsArray();
        Assert.Equal(files.Count - 1, listed.Count);
        foreach (JsonNode? f in listed)
        {
            string name = f!["name"]!.GetValue<string>();
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(files[name])), f["sha256"]!.GetValue<string>());
        }

        string[] expected = ["README.md", "application.json", "sign-in-policy.json", "administrators.csv", "roles.csv", "organisations.csv", "access-list.csv", "access-changes.csv", "audit-events.jsonl", "audit-summary.csv", "audit-chain.txt", "integrations.json", "manifest.json"];
        Assert.Equal(expected.Order(StringComparer.Ordinal), files.Keys.Order(StringComparer.Ordinal));
    }

    [PostgresFact]
    public async Task TheAccessList_AndTheJoinersAndLeavers_ComeFromSangamsRecords()
    {
        Dictionary<string, byte[]> files = Unzip((await BuildAsync(_owner))!.Content);
        string access = Encoding.UTF8.GetString(files["access-list.csv"]);
        string changes = Encoding.UTF8.GetString(files["access-changes.csv"]);

        Assert.Contains(_nurse.ToString(), access, StringComparison.Ordinal);
        Assert.Contains("City Hospital", access, StringComparison.Ordinal);
        Assert.DoesNotContain(_leaver.ToString(), access, StringComparison.Ordinal);
        Assert.Contains(",granted," + _leaver, changes, StringComparison.Ordinal);
        Assert.Contains(",revoked," + _leaver, changes, StringComparison.Ordinal);
        Assert.Contains("owner@lims.test", Encoding.UTF8.GetString(files["administrators.csv"]), StringComparison.Ordinal);
        Assert.Contains("results:read", Encoding.UTF8.GetString(files["roles.csv"]), StringComparison.Ordinal);
        Assert.Contains("INTACT", Encoding.UTF8.GetString(files["audit-chain.txt"]), StringComparison.Ordinal);
    }

    [PostgresFact]
    public async Task NoCell_RunsAsAFormula_AndNoSecretIsIncluded()
    {
        Dictionary<string, byte[]> files = Unzip((await BuildAsync(_owner))!.Content);
        string access = Encoding.UTF8.GetString(files["access-list.csv"]);
        Assert.Contains("\"'=HYPERLINK(", access, StringComparison.Ordinal);
        Assert.DoesNotContain(",=HYPERLINK", access, StringComparison.Ordinal);
        Assert.DoesNotContain(",\"=HYPERLINK", access, StringComparison.Ordinal);
        foreach (byte[] file in files.Values)
        {
            Assert.DoesNotContain("PROTECTED-SECRET-MUST-NOT-LEAK", Encoding.UTF8.GetString(file), StringComparison.Ordinal);
        }

        Assert.Contains("https://lims.example.in/scim/v2", Encoding.UTF8.GetString(files["integrations.json"]), StringComparison.Ordinal);
    }

    [PostgresFact]
    public async Task TheAuditTrail_IsInTheSharedSchema_AndTheDownloadIsAudited()
    {
        EvidencePack pack = (await BuildAsync(_owner))!;
        string[] lines = Encoding.UTF8.GetString(Unzip(pack.Content)["audit-events.jsonl"]).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.NotEmpty(lines);
        foreach (string line in lines)
        {
            JsonNode envelope = JsonNode.Parse(line)!;
            Assert.Empty(SangamAudit.Validate(envelope["event"]));
        }

        Assert.Contains(lines, l => l.Contains("\"identity.org_membership.revoke\"", StringComparison.Ordinal));

        await using SangamDbContext db = _pg.CreateContext();
        AuditEvent export = await db.AuditEvents.AsNoTracking().OrderByDescending(e => e.Id).FirstAsync(e => e.Action == AuditActions.EvidenceExport);
        Assert.Equal(_owner, export.ActorUserId);
        Assert.Equal(_app, export.TargetId);
        using JsonDocument metadata = JsonDocument.Parse(export.Metadata);
        Assert.Equal(pack.Sha256, metadata.RootElement.GetProperty("sha256").GetString());
    }

    [PostgresFact]
    public async Task AnOwnerMadeByASangamOperator_IsInTheAuditTrail()
    {
        // R7: the operator acts in the platform's capacity, so the grant names the application in its detail.
        Guid operatorId = await NewUserAsync("operator@sangam.test", "+919876502005");
        await using (SangamDbContext db = _pg.CreateContext())
        {
            db.PlatformOperators.Add(new PlatformOperator { Id = Guid.NewGuid(), UserId = operatorId, Role = PlatformRole.AppManager, GrantedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }

        using (IServiceScope scope = _provider.CreateScope())
        {
            AdminResult assigned = await scope.ServiceProvider.GetRequiredService<IAdminService>().AssignAppOwnerAsync(operatorId, _app, "nurse@lims.test", null);
            Assert.True(assigned.Succeeded, assigned.Message);
        }

        EvidencePack pack = (await BuildAsync(_owner))!;
        string trail = Encoding.UTF8.GetString(Unzip(pack.Content)["audit-events.jsonl"]);
        Assert.Contains("\"identity.app.admin.grant\"", trail, StringComparison.Ordinal);
        Assert.Contains(operatorId.ToString("D"), trail, StringComparison.Ordinal);
    }

    [PostgresFact]
    public async Task APeriodLongerThanAYear_OrBackwards_IsRefused()
    {
        using IServiceScope scope = _provider.CreateScope();
        IEvidencePackService service = scope.ServiceProvider.GetRequiredService<IEvidencePackService>();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.BuildAsync(_owner, _app, Today, Today.AddDays(-1)));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.BuildAsync(_owner, _app, Today.AddDays(-400), Today));
    }

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("=1+1", "'=1+1")]
    [InlineData("+91 98450", "'+91 98450")]
    [InlineData("-5", "'-5")]
    [InlineData("@SUM(A1)", "'@SUM(A1)")]
    [InlineData("a,b", "\"a,b\"")]
    [InlineData("say \"hi\"", "\"say \"\"hi\"\"\"")]
    [InlineData(null, "")]
    public void ACsvCell_IsQuoted_AndNeverAFormula(string? value, string expected)
        => Assert.Equal(expected, EfEvidencePackService.Cell(value));

    private async Task<EvidencePack?> BuildAsync(Guid userId)
    {
        using IServiceScope scope = _provider.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IEvidencePackService>().BuildAsync(userId, _app, Today.AddDays(-30), Today);
    }

    private static Dictionary<string, byte[]> Unzip(byte[] zip)
    {
        using ZipArchive archive = new(new MemoryStream(zip), ZipArchiveMode.Read);
        Dictionary<string, byte[]> files = new(StringComparer.Ordinal);
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            using Stream stream = entry.Open();
            using MemoryStream buffer = new();
            stream.CopyTo(buffer);
            files[entry.FullName] = buffer.ToArray();
        }

        return files;
    }

    private async Task<Guid> NewUserAsync(string email, string mobile)
    {
        using IServiceScope scope = _provider.CreateScope();
        IAccountService accounts = scope.ServiceProvider.GetRequiredService<IAccountService>();
        InMemoryEmailOutbox outbox = scope.ServiceProvider.GetRequiredService<InMemoryEmailOutbox>();
        Guid id = (await accounts.RegisterAsync(new RegisterUserCommand(
            "Test", email.Split('@')[0], email, mobile, new DateOnly(1985, 1, 1), Gender.PreferNotToSay, "Kaveri-River-2026!", "v1", null, null))).UserId!.Value;
        string code = System.Text.RegularExpressions.Regex.Match(outbox.LatestFor(email)!.Message.TextBody, "[0-9]{6}").Value;
        await accounts.VerifyCodeAsync(id, OneTimeCodePurpose.EmailVerification, code, null);
        return id;
    }

    private sealed class TestEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";

        public string ApplicationName { get; set; } = "Sangam.Tests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
