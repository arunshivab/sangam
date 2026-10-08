using System.Net;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Sangam.Identity.Application.Partners;
using Sangam.Identity.Application.Provisioning;
using Sangam.Identity.Application.Tenancy;
using Sangam.Identity.Domain;
using Sangam.Identity.Domain.Entities;
using Sangam.Identity.Domain.Enums;
using Sangam.Identity.Infrastructure.Persistence;
using Sangam.Identity.Infrastructure.Provisioning;
using Sangam.Identity.Infrastructure.Tests.Accounts;
using Sangam.Identity.Infrastructure.Tests.Postgres;

namespace Sangam.Identity.Infrastructure.Tests.Provisioning;

/// <summary>
/// PR-23 (SGM-216): SCIM 2.0 provisioning against a reference SCIM server — creates, group membership, changes,
/// deactivation, order, retries and giving up, recovery after a server-side deletion, reconciliation, and the partner
/// console's settings with the bearer token kept encrypted.
/// </summary>
[Collection("postgres")]
public sealed class ScimProvisioningTests : IAsyncLifetime
{
    private readonly PostgresFixture _pg;
    private readonly List<IServiceScope> _scopes = [];
    private ServiceProvider _provider = null!;
    private ReferenceScimServer _server = null!;
    private Guid _app;
    private Guid _owner;
    private Guid _nurse;
    private Guid _hospital;
    private Guid _branch;

    public ScimProvisioningTests(PostgresFixture pg)
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
        _server = await ReferenceScimServer.StartAsync();
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Sangam"] = _pg.ConnectionString,
            ["Sangam:Email:UseOutbox"] = "true",
            ["Sangam:Maintenance:Enabled"] = "false",
            ["Sangam:DataProtection:PersistKeys"] = "false",
            ["Sangam:Outbound:AllowPrivateNetworks"] = "true",
        }).Build();
        ServiceCollection services = new();
        services.AddLogging();
        services.AddDataProtection();
        services.AddSingleton(configuration);
        services.AddSangamInfrastructure(configuration);
        _provider = services.BuildServiceProvider();

        DateTimeOffset now = DateTimeOffset.UtcNow;
        await using SangamDbContext db = _pg.CreateContext();
        App lims = new() { Id = Guid.NewGuid(), ClientId = "lipi-lims", Slug = "lipi-lims", DisplayName = "LiPi LIMS", OwnerCompanyName = "imagiQa", CreatedAt = now, UpdatedAt = now };
        db.Apps.Add(lims);
        SangamUser owner = TestUsers.New("owner@lims.test", "+919876502001");
        SangamUser nurse = TestUsers.New("asha@lims.test", "+919876502002");
        nurse.FirstName = "Asha";
        nurse.LastName = "Nair";
        db.Users.AddRange(owner, nurse);
        db.AppAdmins.Add(new AppAdmin { Id = Guid.NewGuid(), AppId = lims.Id, UserId = owner.Id, Role = AppAdminRole.Owner, GrantedAt = now });
        db.AppGrants.Add(new AppGrant { Id = Guid.NewGuid(), AppId = lims.Id, UserId = nurse.Id, GrantedAt = now });
        db.Consents.Add(new Consent { Id = Guid.NewGuid(), AppId = lims.Id, UserId = nurse.Id, Scope = "email openid orgs.read phone profile", ConsentVersion = "v1", GrantedAt = now });
        await db.SaveChangesAsync();
        (_app, _owner, _nurse) = (lims.Id, owner.Id, nurse.Id);
        _hospital = Guid.NewGuid();
        _branch = Guid.NewGuid();
        IManagementService management = Scoped<IManagementService>();
        Assert.True((await management.UpsertRoleAsync(_app, "nurse", new RoleUpsert("Nurse", null, [], null), ManagementActor.Api)).Status == ManagementStatus.Ok);
        Assert.True((await management.UpsertRoleAsync(_app, "lab_tech", new RoleUpsert("Lab technician", null, [], null), ManagementActor.Api)).Status == ManagementStatus.Ok);
        Assert.True((await management.UpsertOrganisationAsync(_app, _hospital, new OrganisationUpsert("Apulki Hospital", "hospital", null, null), ManagementActor.Api)).Status == ManagementStatus.Ok);
        Assert.True((await management.UpsertOrganisationAsync(_app, _branch, new OrganisationUpsert("Apulki Baner", "hospital", null, null), ManagementActor.Api)).Status == ManagementStatus.Ok);
    }

    public async Task DisposeAsync()
    {
        foreach (IServiceScope scope in _scopes)
        {
            scope.Dispose();
        }

        if (_provider is not null)
        {
            await _provider.DisposeAsync();
        }

        if (_server is not null)
        {
            await _server.DisposeAsync();
        }
    }

    [PostgresFact]
    public async Task ARoleGranted_ProvisionsThePerson_InTheGroupForTheRole_WithWhatTheyConsentedTo()
    {
        await SwitchOnAsync();
        await GrantAsync(_hospital, "nurse");
        await RunAsync();

        JsonObject user = Assert.IsType<JsonObject>(_server.UserByExternalId(_nurse));
        Assert.Equal("asha@lims.test", user["userName"]!.GetValue<string>());
        Assert.Equal("Asha", user["name"]!["givenName"]!.GetValue<string>());
        Assert.True(user["active"]!.GetValue<bool>());
        Assert.Equal("+919876502002", user["phoneNumbers"]![0]!["value"]!.GetValue<string>());
        Assert.Equal("Apulki Hospital", user[ScimClient.EnterpriseSchema]!["organization"]!.GetValue<string>());
        Assert.Equal([user["id"]!.GetValue<string>()], _server.MembersOf("Nurse"));

        // Nothing changed: nothing more is sent.
        int before = _server.Requests.Count;
        await RunAsync();
        Assert.Equal(before, _server.Requests.Count);
    }

    [PostgresFact]
    public async Task ChangesAndTheLastRoleRevoked_FollowInOrder_EndingInDeactivation()
    {
        await SwitchOnAsync(mapping: "role_org");
        await GrantAsync(_hospital, "nurse");
        await RunAsync();
        string remote = _server.UserByExternalId(_nurse)!["id"]!.GetValue<string>();

        // A second role elsewhere, a profile change, and the first role taken away — handed over together.
        await GrantAsync(_branch, "lab_tech");
        await using (SangamDbContext db = _pg.CreateContext())
        {
            SangamUser nurse = await db.Users.SingleAsync(u => u.Id == _nurse);
            nurse.LastName = "Menon";
            await AppEventLog.AddAsync(db, AppEventTypes.UserUpdated, null, _nurse, null, null, DateTimeOffset.UtcNow);
            await db.SaveChangesAsync();
        }

        Assert.True((await Scoped<IManagementService>().RevokeMembershipAsync(_app, _hospital, _nurse, ManagementActor.Api)).Status == ManagementStatus.Ok);
        await RunAsync();
        Assert.Equal("Menon", _server.Users[remote]["name"]!["familyName"]!.GetValue<string>());
        Assert.Empty(_server.MembersOf("Nurse — Apulki Hospital"));
        Assert.Equal([remote], _server.MembersOf("Lab technician — Apulki Baner"));

        Assert.True((await Scoped<IManagementService>().RevokeMembershipAsync(_app, _branch, _nurse, ManagementActor.Api)).Status == ManagementStatus.Ok);
        await RunAsync();
        Assert.False(_server.Users[remote]["active"]!.GetValue<bool>());
        Assert.Empty(_server.MembersOf("Lab technician — Apulki Baner"));

        await using SangamDbContext check = _pg.CreateContext();
        Assert.Contains(await check.AppEvents.Select(e => e.Type).ToListAsync(), t => t == AppEventTypes.UserDeactivated);
        Assert.All(await check.ScimDeliveries.ToListAsync(), d => Assert.Equal("done", d.Status));
    }

    [PostgresFact]
    public async Task AFailingServer_IsRetriedWithPauses_ThenGivenUp_AndMarkedFailing_AndCanBeRetried()
    {
        await SwitchOnAsync();
        await GrantAsync(_hospital, "nurse");
        _server.FailNext(100, HttpStatusCode.ServiceUnavailable);
        await Scoped<AppEventDispatcher>().DispatchAsync();
        ScimProvisioner scim = Scoped<ScimProvisioner>();
        await scim.DeliverDueAsync();

        await using (SangamDbContext db = _pg.CreateContext())
        {
            ScimDelivery waiting = await db.ScimDeliveries.SingleAsync();
            Assert.Equal(("pending", 1, 503), (waiting.Status, waiting.Attempts, waiting.LastStatusCode));
            Assert.InRange(waiting.NextAttemptAt - DateTimeOffset.UtcNow, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1));
            Assert.Contains("injected failure", waiting.LastError, StringComparison.Ordinal);

            // Make every pause pass at once, as a day would.
            for (int i = 0; i < ScimProvisioner.Backoff.Count; i++)
            {
                await db.ScimDeliveries.ExecuteUpdateAsync(u => u.SetProperty(d => d.NextAttemptAt, DateTimeOffset.UtcNow.AddMinutes(-1)));
                await Scoped<ScimProvisioner>().DeliverDueAsync();
            }
        }

        await using (SangamDbContext db = _pg.CreateContext())
        {
            ScimDelivery dead = await db.ScimDeliveries.SingleAsync();
            Assert.Equal(("dead", ScimProvisioner.Backoff.Count + 1), (dead.Status, dead.Attempts));
            Assert.Equal("failing", (await db.ScimTargets.SingleAsync()).Status);
            Assert.True(await db.AuditEvents.AnyAsync(e => e.Action == AuditActions.ScimFailing));

            _server.FailNext(0, HttpStatusCode.OK);
            Assert.True((await Scoped<IProvisioningService>().RetryScimAsync(_owner, _app, dead.Id)).Succeeded);
        }

        await RunAsync();
        Assert.NotNull(_server.UserByExternalId(_nurse));
        await using SangamDbContext after = _pg.CreateContext();
        Assert.Equal("ok", (await after.ScimTargets.SingleAsync()).Status);
    }

    [PostgresFact]
    public async Task APersonDeletedOnTheServer_IsCreatedAgain_AndReconciliationRepairsDrift()
    {
        await SwitchOnAsync();
        await GrantAsync(_hospital, "nurse");
        await RunAsync();
        string first = _server.UserByExternalId(_nurse)!["id"]!.GetValue<string>();

        // Someone deletes the person on the application's side; the next change brings them back.
        _server.Users.TryRemove(first, out _);
        foreach (JsonObject group in _server.Groups.Values)
        {
            group["members"] = new JsonArray();
        }

        string summary = await Scoped<ScimProvisioner>().ReconcileAsync(_app);
        Assert.Contains("1 should be active, the server reports 0", summary, StringComparison.Ordinal);
        await Scoped<ScimProvisioner>().DeliverDueAsync();
        JsonObject again = Assert.IsType<JsonObject>(_server.UserByExternalId(_nurse));
        Assert.NotEqual(first, again["id"]!.GetValue<string>());
        Assert.Equal([again["id"]!.GetValue<string>()], _server.MembersOf("Nurse"));
    }

    [PostgresFact]
    public async Task ThePartnerConsole_SavesTheTokenEncrypted_TestsTheServer_AndOnlyForItsAdministrators()
    {
        IProvisioningService service = Scoped<IProvisioningService>();
        Assert.Null(await service.GetScimAsync(_nurse, _app));
        Assert.False((await service.SaveScimAsync(_nurse, _app, Input())).Succeeded);

        Assert.Contains("https", (await service.SaveScimAsync(_owner, _app, Input() with { BaseUrl = "ftp://lims.example.in" })).Message, StringComparison.Ordinal);
        PartnerResult tested = await service.TestScimAsync(_owner, _app, Input());
        Assert.True(tested.Succeeded, tested.Message);
        Assert.False((await service.TestScimAsync(_owner, _app, Input() with { BearerToken = "wrong" })).Succeeded);

        Assert.True((await service.SaveScimAsync(_owner, _app, Input())).Succeeded);
        await using SangamDbContext db = _pg.CreateContext();
        ScimTarget stored = await db.ScimTargets.SingleAsync();
        Assert.DoesNotContain(_server.Token, stored.ProtectedToken, StringComparison.Ordinal);
        ScimSettingsView view = (await service.GetScimAsync(_owner, _app))!;
        Assert.True(view.HasToken);
        Assert.True(view.Enabled);

        // Saved without retyping the token keeps it; the stored one still tests.
        Assert.True((await service.SaveScimAsync(_owner, _app, Input() with { BearerToken = null, GroupMapping = "role_org" })).Succeeded);
        Assert.True((await service.TestScimAsync(_owner, _app, Input() with { BearerToken = null })).Succeeded);
        Assert.True(await db.AuditEvents.AnyAsync(e => e.Action == AuditActions.ScimSettings));
    }

    [Theory]
    [InlineData("https://lims.example.in/scim/v2", false, true)]
    [InlineData("http://lims.example.in/scim/v2", false, false)]
    [InlineData("https://10.0.0.5/scim", false, false)]
    [InlineData("https://[::1]/scim", false, false)]
    [InlineData("https://169.254.169.254/latest", false, false)]
    [InlineData("https://user:pass@lims.example.in/scim", false, false)]
    [InlineData("http://127.0.0.1:5000/scim", true, true)]
    public void OutboundAddresses_AreHttps_AndNeverPrivate_UnlessAllowed(string address, bool allowPrivate, bool accepted)
    {
        Assert.Equal(accepted, OutboundHttp.Check(address, allowPrivate) is null);
    }

    [Theory]
    [InlineData("10.1.2.3", true)]
    [InlineData("172.20.0.1", true)]
    [InlineData("192.168.1.1", true)]
    [InlineData("100.64.0.1", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("::ffff:10.0.0.1", true)]
    [InlineData("fd00::1", true)]
    [InlineData("8.8.8.8", false)]
    [InlineData("2606:4700::1111", false)]
    public void PrivateAddresses_AreRecognised(string address, bool isPrivate)
    {
        Assert.Equal(isPrivate, OutboundHttp.IsPrivate(IPAddress.Parse(address)));
    }

    private ScimSettingsInput Input() => new(_server.BaseUrl, "bearer", _server.Token, "role", false, true);

    private async Task SwitchOnAsync(string mapping = "role")
    {
        PartnerResult saved = await Scoped<IProvisioningService>().SaveScimAsync(_owner, _app, Input() with { GroupMapping = mapping });
        Assert.True(saved.Succeeded, saved.Message);
    }

    private async Task GrantAsync(Guid org, string role)
    {
        ManagementResult<MembershipDto> granted = await Scoped<IManagementService>().UpsertMembershipAsync(_app, org, _nurse, new MembershipUpsert(role, false), ManagementActor.Api);
        Assert.True(granted.Status == ManagementStatus.Ok, granted.Message);
    }

    private async Task RunAsync()
    {
        await Scoped<AppEventDispatcher>().DispatchAsync();
        ScimProvisioner scim = Scoped<ScimProvisioner>();
        for (int i = 0; i < 3 && await scim.DeliverDueAsync() > 0; i++)
        {
            scim = Scoped<ScimProvisioner>();
        }
    }

    private T Scoped<T>()
        where T : notnull
    {
        IServiceScope scope = _provider.CreateScope();
        _scopes.Add(scope);
        return scope.ServiceProvider.GetRequiredService<T>();
    }
}
