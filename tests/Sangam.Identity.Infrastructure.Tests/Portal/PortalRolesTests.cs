using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Sangam.Identity.Application.Portal;
using Sangam.Identity.Domain;
using Sangam.Identity.Domain.Entities;
using Sangam.Identity.Domain.Enums;
using Sangam.Identity.Infrastructure.Persistence;
using Sangam.Identity.Infrastructure.Services;
using Sangam.Identity.Infrastructure.Tests.Accounts;
using Sangam.Identity.Infrastructure.Tests.Postgres;

namespace Sangam.Identity.Infrastructure.Tests.Portal;

/// <summary>
/// rc.10 (P4; founder decisions, 12 Oct 2026): a person sees their roles by organisation, in each application's own words,
/// and can leave an organisation. Leaving ends the roles held there through the partner's own path (audited, the
/// application told), tells the person and every organisation admin, and never strands an application without one.
/// </summary>
[Collection("postgres")]
public sealed class PortalRolesTests : IAsyncLifetime
{
    private readonly List<IServiceScope> _scopes = [];
    private readonly PostgresFixture _pg;
    private ServiceProvider _provider = null!;
    private Guid _his;
    private Guid _lab;
    private Guid _group;
    private Guid _hospital;
    private Guid _doctor;
    private Guid _hisOrgAdmin;
    private Guid _labOrgAdmin;
    private Guid _person;
    private Guid _otherAdmin;

    public PortalRolesTests(PostgresFixture pg)
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
        ServiceCollection services = new();
        services.AddLogging();
        services.AddDataProtection();
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Sangam"] = _pg.ConnectionString,
            ["Sangam:Email:UseOutbox"] = "true",
            ["Sangam:Maintenance:Enabled"] = "false",
        }).Build();
        services.AddSingleton(configuration);
        services.AddSangamInfrastructure(configuration);
        _provider = services.BuildServiceProvider();

        DateTimeOffset now = DateTimeOffset.UtcNow;
        await using SangamDbContext db = _pg.CreateContext();
        App his = new() { Id = Guid.NewGuid(), ClientId = "his-roles", Slug = "his-roles", DisplayName = "LiPi HIS", OwnerCompanyName = "imagiQa", CreatedAt = now, UpdatedAt = now };
        App lab = new() { Id = Guid.NewGuid(), ClientId = "lab-roles", Slug = "lab-roles", DisplayName = "LiPi Lab", OwnerCompanyName = "imagiQa", CreatedAt = now, UpdatedAt = now };
        db.Apps.AddRange(his, lab);
        Guid groupId = Guid.NewGuid();
        Organisation group = new() { Id = groupId, Name = "Apulki Group", OrgTypeCode = "hospital", Path = OrganisationPath.ForRoot(groupId), RegisteredViaAppId = his.Id, CreatedAt = now, UpdatedAt = now };
        Guid hospitalId = Guid.NewGuid();
        Organisation hospital = new() { Id = hospitalId, Name = "Apulki Medical Center", OrgTypeCode = "hospital", ParentOrgId = groupId, Path = OrganisationPath.ForChild(group.Path, hospitalId), Depth = 1, RegisteredViaAppId = his.Id, CreatedAt = now, UpdatedAt = now };
        db.Organisations.AddRange(group, hospital);
        Role doctor = new() { Id = Guid.NewGuid(), AppId = his.Id, Code = "doc", DisplayName = "Doctor", CreatedAt = now };
        Role hisAdmin = new() { Id = Guid.NewGuid(), AppId = his.Id, Code = "org_admin", DisplayName = "Organisation admin", IsSystem = true, CreatedAt = now };
        Role labAdmin = new() { Id = Guid.NewGuid(), AppId = lab.Id, Code = "org_admin", DisplayName = string.Empty, IsSystem = true, CreatedAt = now };
        db.Roles.AddRange(doctor, hisAdmin, labAdmin);
        SangamUser person = TestUsers.New("ravi@roles.test", null);
        person.FirstName = "Ravi";
        person.LastName = "Menon";
        SangamUser other = TestUsers.New("asha@roles.test", null);
        other.FirstName = "Asha";
        db.Users.AddRange(person, other);
        await db.SaveChangesAsync();
        (_his, _lab, _group, _hospital, _doctor, _hisOrgAdmin, _labOrgAdmin, _person, _otherAdmin) = (his.Id, lab.Id, groupId, hospitalId, doctor.Id, hisAdmin.Id, labAdmin.Id, person.Id, other.Id);
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

    [PostgresFact]
    public async Task Roles_AreGroupedByOrganisation_InTheApplicationsOwnWords()
    {
        await GrantAsync(_person, _hospital, _his, _doctor, grantedBy: _otherAdmin, expires: DateTimeOffset.UtcNow.AddDays(10));
        await GrantAsync(_person, _group, _lab, _labOrgAdmin, beneath: true);

        IReadOnlyList<PortalOrganisation> orgs = await Service().GetAsync(_person);
        Assert.Equal(2, orgs.Count);
        PortalOrganisation hospital = Assert.Single(orgs, o => o.OrgId == _hospital);
        Assert.Equal("Apulki Group", hospital.Within);
        PortalRole doctor = Assert.Single(hospital.Roles);
        Assert.Equal("Doctor", doctor.RoleName);
        Assert.Equal("Asha User", doctor.GivenBy);
        Assert.NotNull(doctor.Until);

        // An application that named no role is shown by its code; a role given at the group holds beneath it.
        PortalRole groupRole = Assert.Single(Assert.Single(orgs, o => o.OrgId == _group).Roles);
        Assert.Equal("org_admin", groupRole.RoleName);
        Assert.True(groupRole.AppliesBeneath);
        Assert.True(groupRole.OnlyOrganisationAdmin);
    }

    [PostgresFact]
    public async Task Leaving_EndsTheRolesThere_TellsTheApplication_ThePerson_AndEveryOrganisationAdmin()
    {
        await GrantAsync(_person, _hospital, _his, _doctor);
        await GrantAsync(_otherAdmin, _hospital, _his, _hisOrgAdmin);
        // A role at the group above is not touched by leaving the hospital.
        await GrantAsync(_person, _group, _his, _doctor, beneath: true);
        // The HIS listens (a webhook), so the events it is sent are recorded.
        await using (SangamDbContext seed = _pg.CreateContext())
        {
            seed.WebhookEndpoints.Add(new WebhookEndpoint { Id = Guid.NewGuid(), AppId = _his, Url = "https://his.example.in/hooks", Events = "membership.revoked", ProtectedSecret = "x", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
            await seed.SaveChangesAsync();
        }

        LeaveResult result = await Service().LeaveAsync(_person, _hospital, "10.4.2.37");
        Assert.True(result.Succeeded);
        Assert.Equal(["LiPi HIS"], result.Ended);
        Assert.Empty(result.Kept);

        await using SangamDbContext db = _pg.CreateContext();
        Assert.NotNull((await db.OrgMemberships.SingleAsync(m => m.UserId == _person && m.OrgId == _hospital)).RevokedAt);
        Assert.Null((await db.OrgMemberships.SingleAsync(m => m.UserId == _person && m.OrgId == _group)).RevokedAt);

        AuditEvent revoke = await db.AuditEvents.SingleAsync(e => e.Action == AuditActions.OrgMembershipRevoke && e.TargetId == _person);
        Assert.Equal(AuditActorType.User, revoke.ActorType);
        Assert.Equal(_person, revoke.ActorUserId);
        AuditEvent leave = await db.AuditEvents.SingleAsync(e => e.Action == AuditActions.UserOrganisationLeave);
        Assert.Equal("10.4.2.37", leave.IpAddress);
        Assert.Contains("Apulki Medical Center", leave.Metadata, StringComparison.Ordinal);
        Assert.True(await db.AppEvents.AnyAsync(e => e.AppId == _his && e.UserId == _person && e.Type == AppEventTypes.MembershipRevoked));
        // Ravi still holds the group's role in the HIS, so the application is not told he is gone.
        Assert.False(await db.AppEvents.AnyAsync(e => e.AppId == _his && e.UserId == _person && e.Type == AppEventTypes.UserDeactivated));

        InMemoryEmailOutbox outbox = _provider.GetRequiredService<InMemoryEmailOutbox>();
        Assert.Contains(outbox.Recent, m => m.Message.ToEmail == "ravi@roles.test" && m.Message.Subject.Contains("You left Apulki Medical Center", StringComparison.Ordinal) && m.Message.TextBody.Contains("LiPi HIS: Doctor", StringComparison.Ordinal));
        Assert.Contains(outbox.Recent, m => m.Message.ToEmail == "asha@roles.test" && m.Message.Subject.Contains("Ravi Menon left Apulki Medical Center", StringComparison.Ordinal));
    }

    [PostgresFact]
    public async Task TheOnlyOrganisationAdmin_KeepsThatRole_AndLeavesTheRest()
    {
        await GrantAsync(_person, _hospital, _his, _hisOrgAdmin);
        await GrantAsync(_person, _hospital, _lab, _labOrgAdmin);
        await GrantAsync(_otherAdmin, _hospital, _lab, _labOrgAdmin);

        PortalOrganisation hospital = Assert.Single(await Service().GetAsync(_person));
        Assert.True(hospital.CanLeave);
        Assert.True(Assert.Single(hospital.Roles, r => r.AppId == _his).OnlyOrganisationAdmin);
        Assert.False(Assert.Single(hospital.Roles, r => r.AppId == _lab).OnlyOrganisationAdmin);

        LeaveResult result = await Service().LeaveAsync(_person, _hospital, null);
        Assert.Equal(["LiPi Lab"], result.Ended);
        Assert.Equal(["LiPi HIS"], result.Kept);

        await using SangamDbContext db = _pg.CreateContext();
        Assert.Null((await db.OrgMemberships.SingleAsync(m => m.UserId == _person && m.AppId == _his)).RevokedAt);
        Assert.NotNull((await db.OrgMemberships.SingleAsync(m => m.UserId == _person && m.AppId == _lab)).RevokedAt);

        // Now only the HIS role is left, and it cannot be left.
        Assert.False(Assert.Single(await Service().GetAsync(_person)).CanLeave);
        Assert.False((await Service().LeaveAsync(_person, _hospital, null)).Succeeded);
    }

    [PostgresFact]
    public async Task NobodyElsesRoles_AreShownOrEnded()
    {
        await GrantAsync(_otherAdmin, _hospital, _his, _doctor);
        Assert.Empty(await Service().GetAsync(_person));
        Assert.False((await Service().LeaveAsync(_person, _hospital, null)).Succeeded);
        await using SangamDbContext db = _pg.CreateContext();
        Assert.Null((await db.OrgMemberships.SingleAsync(m => m.UserId == _otherAdmin)).RevokedAt);
    }

    private async Task GrantAsync(Guid userId, Guid orgId, Guid appId, Guid roleId, Guid? grantedBy = null, DateTimeOffset? expires = null, bool beneath = false)
    {
        await using SangamDbContext db = _pg.CreateContext();
        db.OrgMemberships.Add(new OrgMembership
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            OrgId = orgId,
            AppId = appId,
            RoleId = roleId,
            GrantedByUserId = grantedBy,
            GrantedAt = DateTimeOffset.UtcNow,
            ExpiresAt = expires,
            AppliesToDescendants = beneath,
        });
        await db.SaveChangesAsync();
    }

    private IPortalRolesService Service()
    {
        IServiceScope scope = _provider.CreateScope();
        _scopes.Add(scope);
        return scope.ServiceProvider.GetRequiredService<IPortalRolesService>();
    }
}
