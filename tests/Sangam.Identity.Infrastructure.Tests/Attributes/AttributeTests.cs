using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Sangam.Identity.Application.Attributes;
using Sangam.Identity.Application.Partners;
using Sangam.Identity.Application.Provisioning;
using Sangam.Identity.Application.Tenancy;
using Sangam.Identity.Domain;
using Sangam.Identity.Domain.Entities;
using Sangam.Identity.Domain.Enums;
using Sangam.Identity.Infrastructure.Attributes;
using Sangam.Identity.Infrastructure.Persistence;
using Sangam.Identity.Infrastructure.Tenancy;
using Sangam.Identity.Infrastructure.Tests.Accounts;
using Sangam.Identity.Infrastructure.Tests.Postgres;

namespace Sangam.Identity.Infrastructure.Tests.Attributes;

/// <summary>
/// PR-25 (SGM-209, SGM-202): custom attributes and claims, and time-limited memberships. Attributes refuse health data, check
/// their values' kinds, apply to an organisation's members only when scoped to it, and are changed by a person only when
/// marked theirs; claims cannot shadow Sangam's own; a membership stops counting the moment it ends and the sweep then
/// revokes it and tells SCIM and webhooks.
/// </summary>
[Collection("postgres")]
public sealed class AttributeTests : IAsyncLifetime
{
    private readonly PostgresFixture _pg;
    private readonly List<IServiceScope> _scopes = [];
    private ServiceProvider _provider = null!;
    private Guid _app;
    private Guid _owner;
    private Guid _nurse;
    private Guid _stranger;
    private Guid _hospital;
    private Guid _clinicWing;

    public AttributeTests(PostgresFixture pg)
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
        App his = new() { Id = Guid.NewGuid(), ClientId = "lipi-his", Slug = "lipi-his", DisplayName = "LiPi HIS", OwnerCompanyName = "imagiQa", CreatedAt = now, UpdatedAt = now };
        db.Apps.Add(his);
        SangamUser owner = TestUsers.New("owner@his.test", "+919876504001");
        SangamUser nurse = TestUsers.New("meera@his.test", "+919876504002");
        SangamUser stranger = TestUsers.New("ravi@elsewhere.test", "+919876504003");
        db.Users.AddRange(owner, nurse, stranger);
        db.AppAdmins.Add(new AppAdmin { Id = Guid.NewGuid(), AppId = his.Id, UserId = owner.Id, Role = AppAdminRole.Owner, GrantedAt = now });
        db.AppGrants.Add(new AppGrant { Id = Guid.NewGuid(), AppId = his.Id, UserId = nurse.Id, GrantedAt = now });
        await db.SaveChangesAsync();
        (_app, _owner, _nurse, _stranger) = (his.Id, owner.Id, nurse.Id, stranger.Id);
        _hospital = Guid.NewGuid();
        _clinicWing = Guid.NewGuid();
        IManagementService management = Scoped<IManagementService>();
        await management.UpsertRoleAsync(_app, "nurse", new RoleUpsert("Nurse", null, ["ward.read"], null), ManagementActor.Api);
        Assert.Equal(ManagementStatus.Ok, (await management.UpsertOrganisationAsync(_app, _hospital, new OrganisationUpsert("Apulki Hospital", "hospital", null, null), ManagementActor.Api)).Status);
        Assert.Equal(ManagementStatus.Ok, (await management.UpsertOrganisationAsync(_app, _clinicWing, new OrganisationUpsert("East Wing", "hospital", null, null), ManagementActor.Api)).Status);

        // An enabled webhook endpoint, so events are recorded for the application.
        WebhookResult hook = await Scoped<IWebhookService>().AddAsync(_owner, _app, new WebhookEndpointInput("http://127.0.0.1:9/hook", [.. AppEventTypes.All.Where(t => t != AppEventTypes.Ping)], "Ward system", true));
        Assert.True(hook.Succeeded, hook.Message);
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
    }

    [Theory]
    [InlineData("Blood group", true)]
    [InlineData("hiv_status", true)]
    [InlineData("Current medication", true)]
    [InlineData("Diagnosis code", true)]
    [InlineData("Employee number", false)]
    [InlineData("Archive number", false)]
    [InlineData("Submitter id", false)]
    [InlineData("Departmental code", false)]
    [InlineData("Ward", false)]
    public void TheHealthGuard_ReadsWords_NotSubstrings(string text, bool refused)
    {
        Assert.Equal(refused, EfAttributeService.LooksLikeHealthData(text));
    }

    [PostgresFact]
    public async Task Definitions_FollowTheRules_AndOnlyAdministratorsMakeThem()
    {
        IAttributeService service = Scoped<IAttributeService>();
        Assert.False((await service.DefineAsync(_owner, _app, Definition("blood_group", "Blood group"))).Succeeded);
        Assert.False((await service.DefineAsync(_owner, _app, Definition("employee_no", "Employee number") with { NotHealthData = false })).Succeeded);
        Assert.False((await service.DefineAsync(_owner, _app, Definition("Employee No", "Employee number"))).Succeeded);
        Assert.False((await service.DefineAsync(_owner, _app, Definition("shift", "Shift", "choice") with { Choices = "day" })).Succeeded);
        Assert.False((await service.DefineAsync(_owner, _app, Definition("shift", "Shift", "colour"))).Succeeded);
        Assert.False((await service.DefineAsync(_nurse, _app, Definition("employee_no", "Employee number"))).Succeeded);

        Assert.True((await service.DefineAsync(_owner, _app, Definition("employee_no", "Employee number"))).Succeeded);
        Assert.False((await service.DefineAsync(_owner, _app, Definition("employee_no", "Employee number"))).Succeeded);
        Assert.True((await service.DefineAsync(_owner, _app, Definition("shift", "Shift", "choice") with { Choices = "day, night ,day" })).Succeeded);

        AttributesView view = (await service.GetAsync(_owner, _app))!;
        Assert.Equal(["employee_no", "shift"], view.Definitions.Select(d => d.Key));
        Assert.Equal("day,night", view.Definitions[1].Choices);
        Assert.Null(await service.GetAsync(_nurse, _app));

        for (int i = view.Definitions.Count; i < EfAttributeService.MaxDefinitions; i++)
        {
            Assert.True((await service.DefineAsync(_owner, _app, Definition("extra_" + i, "Extra " + i))).Succeeded);
        }

        Assert.False((await service.DefineAsync(_owner, _app, Definition("one_too_many", "One too many"))).Succeeded);

        await using SangamDbContext db = _pg.CreateContext();
        Assert.True(await db.AuditEvents.AnyAsync(e => e.Action == AuditActions.AttributeDefine));
    }

    [PostgresFact]
    public async Task Values_AreCheckedByKind_AndSetOnlyForPeopleWhoLinkedTheApplication()
    {
        IAttributeService service = Scoped<IAttributeService>();
        await DefineAllAsync(service);

        Assert.False((await service.SetValuesAsync(_owner, _app, _nurse, new Dictionary<string, string?> { ["grade"] = "senior" })).Succeeded);
        Assert.False((await service.SetValuesAsync(_owner, _app, _nurse, new Dictionary<string, string?> { ["joined"] = "2026-13-01" })).Succeeded);
        Assert.False((await service.SetValuesAsync(_owner, _app, _nurse, new Dictionary<string, string?> { ["shift"] = "evening" })).Succeeded);
        Assert.False((await service.SetValuesAsync(_owner, _app, _nurse, new Dictionary<string, string?> { ["on_call"] = "maybe" })).Succeeded);
        Assert.False((await service.SetValuesAsync(_owner, _app, _nurse, new Dictionary<string, string?> { ["employee_no"] = new string('7', 201) })).Succeeded);
        Assert.False((await service.SetValuesAsync(_owner, _app, _nurse, new Dictionary<string, string?> { ["unknown"] = "x" })).Succeeded);
        Assert.False((await service.SetValuesAsync(_owner, _app, _stranger, new Dictionary<string, string?> { ["employee_no"] = "E-1" })).Succeeded);
        Assert.Null(await service.GetValuesAsync(_owner, _app, _stranger));

        PartnerResult saved = await service.SetValuesAsync(_owner, _app, _nurse, new Dictionary<string, string?>
        {
            ["employee_no"] = " E-1042 ",
            ["grade"] = "07",
            ["joined"] = "2024-04-01",
            ["shift"] = "night",
            ["on_call"] = "true",
        });
        Assert.True(saved.Succeeded, saved.Message);
        IReadOnlyList<AttributeValueRow> rows = (await service.GetValuesAsync(_owner, _app, _nurse))!;
        Assert.Equal("E-1042", rows.Single(r => r.Key == "employee_no").Value);
        Assert.Equal("7", rows.Single(r => r.Key == "grade").Value);

        // The wing's attribute applies only to people with a role there.
        Assert.DoesNotContain(rows, r => r.Key == "locker");
        Assert.False((await service.SetValuesAsync(_owner, _app, _nurse, new Dictionary<string, string?> { ["locker"] = "12" })).Succeeded);
        Assert.Equal(ManagementStatus.Ok, (await Scoped<IManagementService>().UpsertMembershipAsync(_app, _clinicWing, _nurse, new MembershipUpsert("nurse", false), ManagementActor.Api)).Status);
        Assert.True((await service.SetValuesAsync(_owner, _app, _nurse, new Dictionary<string, string?> { ["locker"] = "12" })).Succeeded);

        // Clearing a value removes it; unchanged values raise nothing.
        Assert.True((await service.SetValuesAsync(_owner, _app, _nurse, new Dictionary<string, string?> { ["on_call"] = "" })).Succeeded);
        Assert.Null((await service.GetValuesAsync(_owner, _app, _nurse))!.Single(r => r.Key == "on_call").Value);
        Assert.Equal("Nothing changed.", (await service.SetValuesAsync(_owner, _app, _nurse, new Dictionary<string, string?> { ["shift"] = "night" })).Message);

        await using SangamDbContext db = _pg.CreateContext();
        Assert.Equal(3, await db.AppEvents.CountAsync(e => e.Type == AppEventTypes.UserUpdated && e.UserId == _nurse));
        Assert.True(await db.AuditEvents.AnyAsync(e => e.Action == AuditActions.AttributeValuesSet && e.TargetId == _nurse));
    }

    [PostgresFact]
    public async Task APerson_SeesWhatTheApplicationKeeps_AndChangesOnlyTheirOwnRows()
    {
        IAttributeService service = Scoped<IAttributeService>();
        await DefineAllAsync(service);
        Assert.True((await service.SetValuesAsync(_owner, _app, _nurse, new Dictionary<string, string?> { ["employee_no"] = "E-1042" })).Succeeded);

        PersonAttributeGroup group = Assert.Single(await service.GetMineAsync(_nurse));
        Assert.Equal("LiPi HIS", group.AppName);
        Assert.Equal("E-1042", group.Values.Single(v => v.Key == "employee_no").Value);
        Assert.Equal("person", group.Values.Single(v => v.Key == "shift").EditableBy);

        Assert.False((await service.SetMineAsync(_nurse, _app, new Dictionary<string, string?> { ["employee_no"] = "E-9999" })).Succeeded);
        Assert.True((await service.SetMineAsync(_nurse, _app, new Dictionary<string, string?> { ["shift"] = "day" })).Succeeded);
        Assert.False((await service.SetMineAsync(_stranger, _app, new Dictionary<string, string?> { ["shift"] = "day" })).Succeeded);
        Assert.Empty(await service.GetMineAsync(_stranger));

        IReadOnlyList<AttributeValueRow> rows = (await service.GetValuesAsync(_owner, _app, _nurse))!;
        Assert.Equal("E-1042", rows.Single(r => r.Key == "employee_no").Value);
        Assert.Equal("day", rows.Single(r => r.Key == "shift").Value);
    }

    [PostgresFact]
    public async Task Claims_CannotShadowSangamsOwn_AndCarryTypedValuesAndRoles()
    {
        IAttributeService service = Scoped<IAttributeService>();
        await DefineAllAsync(service);
        Assert.False((await service.AddClaimAsync(_owner, _app, new ClaimMappingInput("email", "attribute", "employee_no"))).Succeeded);
        Assert.False((await service.AddClaimAsync(_owner, _app, new ClaimMappingInput("sangam_extra", "roles", null))).Succeeded);
        Assert.False((await service.AddClaimAsync(_owner, _app, new ClaimMappingInput("badge", "attribute", "nothing_here"))).Succeeded);
        Assert.False((await service.AddClaimAsync(_owner, _app, new ClaimMappingInput("badge", "everything", null))).Succeeded);
        Assert.True((await service.AddClaimAsync(_owner, _app, new ClaimMappingInput("employee_no", "attribute", "employee_no"))).Succeeded);
        Assert.True((await service.AddClaimAsync(_owner, _app, new ClaimMappingInput("grade", "attribute", "grade"))).Succeeded);
        Assert.True((await service.AddClaimAsync(_owner, _app, new ClaimMappingInput("on_call", "attribute", "on_call"))).Succeeded);
        Assert.True((await service.AddClaimAsync(_owner, _app, new ClaimMappingInput("his_roles", "roles", null))).Succeeded);
        Assert.True((await service.AddClaimAsync(_owner, _app, new ClaimMappingInput("his_permissions", "permissions", null))).Succeeded);
        Assert.False((await service.AddClaimAsync(_owner, _app, new ClaimMappingInput("his_roles", "org_names", null))).Succeeded);

        Assert.True((await service.SetValuesAsync(_owner, _app, _nurse, new Dictionary<string, string?> { ["employee_no"] = "E-1042", ["grade"] = "7", ["on_call"] = "false" })).Succeeded);
        Assert.Equal(ManagementStatus.Ok, (await Scoped<IManagementService>().UpsertMembershipAsync(_app, _hospital, _nurse, new MembershipUpsert("nurse", true), ManagementActor.Api)).Status);

        IReadOnlyDictionary<string, object> claims = await service.ClaimsAsync(_nurse, _app);
        Assert.Equal("E-1042", claims["employee_no"]);
        Assert.Equal(7m, claims["grade"]);
        Assert.Equal(false, claims["on_call"]);
        Assert.Equal(["nurse"], (string[])claims["his_roles"]);
        Assert.Equal(["ward.read"], (string[])claims["his_permissions"]);

        // A retired attribute is no longer released.
        AttributesView view = (await service.GetAsync(_owner, _app))!;
        Assert.True((await service.RetireAsync(_owner, _app, view.Definitions.Single(d => d.Key == "employee_no").Id)).Succeeded);
        Assert.False((await service.ClaimsAsync(_nurse, _app)).ContainsKey("employee_no"));
        Assert.Empty(await service.ClaimsAsync(_stranger, _app));
    }

    [PostgresFact]
    public async Task ATimeLimitedMembership_StopsCountingAtItsEnd_AndTheSweepRevokesIt()
    {
        IManagementService management = Scoped<IManagementService>();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        Assert.NotEqual(ManagementStatus.Ok, (await management.UpsertMembershipAsync(_app, _hospital, _nurse, new MembershipUpsert("nurse", true, now.AddMinutes(-1)), ManagementActor.Api)).Status);
        Assert.NotEqual(ManagementStatus.Ok, (await management.UpsertMembershipAsync(_app, _hospital, _nurse, new MembershipUpsert("nurse", true, now.AddYears(6)), ManagementActor.Api)).Status);
        Assert.Equal(ManagementStatus.Ok, (await management.UpsertMembershipAsync(_app, _hospital, _nurse, new MembershipUpsert("nurse", true, now.AddDays(30)), ManagementActor.Api)).Status);

        Assert.Single(await Scoped<ITenancyQuery>().GetOrgClaimsAsync(_nurse, _app));
        MembershipDto listed = Assert.Single(await management.ListMembersAsync(_app, _hospital));
        Assert.NotNull(listed.ExpiresAt);

        // Its time comes: from that moment it no longer counts, before any sweep runs.
        await using (SangamDbContext db = _pg.CreateContext())
        {
            OrgMembership membership = await db.OrgMemberships.SingleAsync(m => m.UserId == _nurse);
            membership.ExpiresAt = now.AddSeconds(-5);
            await db.SaveChangesAsync();
        }

        Assert.Empty(await Scoped<ITenancyQuery>().GetOrgClaimsAsync(_nurse, _app));
        Assert.Empty(await management.ListMembersAsync(_app, _hospital));

        Assert.Equal(1, await Scoped<MembershipExpiry>().RunAsync());
        Assert.Equal(0, await Scoped<MembershipExpiry>().RunAsync());

        await using SangamDbContext check = _pg.CreateContext();
        OrgMembership revoked = await check.OrgMemberships.SingleAsync(m => m.UserId == _nurse);
        Assert.NotNull(revoked.RevokedAt);
        AppEvent gone = await check.AppEvents.SingleAsync(e => e.Type == AppEventTypes.MembershipRevoked && e.UserId == _nurse);
        Assert.Contains("expired", gone.Data, StringComparison.Ordinal);
        Assert.True(await check.AppEvents.AnyAsync(e => e.Type == AppEventTypes.UserDeactivated && e.UserId == _nurse));
        Assert.True(await check.AuditEvents.AnyAsync(e => e.Action == AuditActions.OrgMembershipExpire && e.TargetId == _nurse));
    }

    private static AttributeDefinitionInput Definition(string key, string label, string type = "text") => new(key, label, type, null, "admin", null, true);

    private async Task DefineAllAsync(IAttributeService service)
    {
        Assert.True((await service.DefineAsync(_owner, _app, Definition("employee_no", "Employee number"))).Succeeded);
        Assert.True((await service.DefineAsync(_owner, _app, Definition("grade", "Pay grade", "number"))).Succeeded);
        Assert.True((await service.DefineAsync(_owner, _app, Definition("joined", "Joined", "date"))).Succeeded);
        Assert.True((await service.DefineAsync(_owner, _app, Definition("shift", "Shift", "choice") with { Choices = "day,night", EditableBy = "person" })).Succeeded);
        Assert.True((await service.DefineAsync(_owner, _app, Definition("on_call", "On call", "boolean"))).Succeeded);
        PartnerResult locker = await service.DefineAsync(_owner, _app, Definition("locker", "Locker") with { OrgId = _clinicWing });
        Assert.True(locker.Succeeded, locker.Message);
    }

    private T Scoped<T>()
        where T : notnull
    {
        IServiceScope scope = _provider.CreateScope();
        _scopes.Add(scope);
        return scope.ServiceProvider.GetRequiredService<T>();
    }
}
