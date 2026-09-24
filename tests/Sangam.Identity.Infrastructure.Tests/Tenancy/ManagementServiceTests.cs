using Microsoft.EntityFrameworkCore;
using Sangam.Identity.Application.Abstractions;
using Sangam.Identity.Application.Tenancy;
using Sangam.Identity.Domain.Entities;
using Sangam.Identity.Domain.Enums;
using Sangam.Identity.Infrastructure.Persistence;
using Sangam.Identity.Infrastructure.Tenancy;
using Sangam.Identity.Infrastructure.Tests.Accounts;
using Sangam.Identity.Infrastructure.Tests.Postgres;

namespace Sangam.Identity.Infrastructure.Tests.Tenancy;

/// <summary>The management API's rules, against a real PostgreSQL.</summary>
[Collection("postgres")]
public sealed class ManagementServiceTests : IAsyncLifetime
{
    private static readonly RoleUpsert Doctor = new("Doctor", "Treats patients", ["patient:read", "rx:write"], null);
    private readonly List<SangamDbContext> _contexts = [];
    private readonly PostgresFixture _pg;
    private Guid _appId;
    private Guid _otherAppId;
    private Guid _userId;

    public ManagementServiceTests(PostgresFixture pg)
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
        await using SangamDbContext db = _pg.CreateContext();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        App app = new() { Id = Guid.NewGuid(), ClientId = "his", Slug = "his", DisplayName = "HIS", OwnerCompanyName = "LiPi", CreatedAt = now, UpdatedAt = now };
        App other = new() { Id = Guid.NewGuid(), ClientId = "aran", Slug = "aran", DisplayName = "Aran", OwnerCompanyName = "imagiQa", CreatedAt = now, UpdatedAt = now };
        SangamUser user = TestUsers.New("member@example.in", "+919000000011");
        db.Apps.AddRange(app, other);
        db.Users.Add(user);
        await db.SaveChangesAsync();
        _appId = app.Id;
        _otherAppId = other.Id;
        _userId = user.Id;
    }

    public async Task DisposeAsync()
    {
        foreach (SangamDbContext context in _contexts)
        {
            await context.DisposeAsync();
        }
    }

    [PostgresFact]
    public async Task UpsertRole_CreatesThenUpdates_AndValidatesTheCode()
    {
        EfManagementService mgmt = Create(out _);

        ManagementResult<RoleDto> created = await mgmt.UpsertRoleAsync(_appId, "doctor", Doctor);
        Assert.Equal(ManagementStatus.Ok, created.Status);
        Assert.Equal(["patient:read", "rx:write"], created.Value!.Permissions);

        ManagementResult<RoleDto> updated = await mgmt.UpsertRoleAsync(_appId, "doctor", Doctor with { DisplayName = "Consultant", Permissions = ["patient:read"] });
        Assert.Equal("Consultant", updated.Value!.DisplayName);
        Assert.Single(updated.Value.Permissions);
        Assert.Single(await mgmt.ListRolesAsync(_appId));

        Assert.Equal(ManagementStatus.Invalid, (await mgmt.UpsertRoleAsync(_appId, "Bad Code", Doctor)).Status);
        Assert.Equal(ManagementStatus.Invalid, (await mgmt.UpsertRoleAsync(_appId, "doctor", Doctor with { DisplayName = " " })).Status);
        Assert.Equal(ManagementStatus.NotFound, (await mgmt.UpsertRoleAsync(_appId, "doctor", Doctor with { OrgId = Guid.NewGuid() })).Status);
    }

    [PostgresFact]
    public async Task Roles_AreInvisibleToOtherApps()
    {
        EfManagementService mgmt = Create(out _);
        await mgmt.UpsertRoleAsync(_appId, "doctor", Doctor);

        Assert.Empty(await mgmt.ListRolesAsync(_otherAppId));
        Assert.Equal(ManagementStatus.NotFound, (await mgmt.RetireRoleAsync(_otherAppId, "doctor")).Status);
    }

    [PostgresFact]
    public async Task UpsertOrganisation_BuildsTheTree_AndEnforcesTypeRules()
    {
        EfManagementService mgmt = Create(out _);
        Guid corpId = Guid.NewGuid();
        Guid hospitalId = Guid.NewGuid();
        Guid deptId = Guid.NewGuid();

        ManagementResult<OrganisationDto> corp = await mgmt.UpsertOrganisationAsync(_appId, corpId, new OrganisationUpsert("Apulki Group", "corporate", null, "{\"pan\":\"X\"}"));
        ManagementResult<OrganisationDto> hospital = await mgmt.UpsertOrganisationAsync(_appId, hospitalId, new OrganisationUpsert("Apulki Medical Center", "hospital", corpId, null));
        ManagementResult<OrganisationDto> dept = await mgmt.UpsertOrganisationAsync(_appId, deptId, new OrganisationUpsert("Oncology", "department", hospitalId, null));

        Assert.Equal(0, corp.Value!.Depth);
        Assert.Equal(2, dept.Value!.Depth);
        Assert.StartsWith(hospital.Value!.Path, dept.Value.Path, StringComparison.Ordinal);

        // A department cannot be a root, and cannot have children.
        Assert.Equal(ManagementStatus.Invalid, (await mgmt.UpsertOrganisationAsync(_appId, Guid.NewGuid(), new OrganisationUpsert("Loose", "department", null, null))).Status);
        Assert.Equal(ManagementStatus.Invalid, (await mgmt.UpsertOrganisationAsync(_appId, Guid.NewGuid(), new OrganisationUpsert("Ward", "department", deptId, null))).Status);
        Assert.Equal(ManagementStatus.NotFound, (await mgmt.UpsertOrganisationAsync(_appId, Guid.NewGuid(), new OrganisationUpsert("Odd", "not-a-type", null, null))).Status);
        Assert.Equal(ManagementStatus.Invalid, (await mgmt.UpsertOrganisationAsync(_appId, Guid.NewGuid(), new OrganisationUpsert("Bad meta", "clinic", null, "[]"))).Status);

        // Another app can neither see it nor edit it.
        Assert.Null(await mgmt.GetOrganisationAsync(_otherAppId, corpId));
        Assert.Equal(ManagementStatus.NotFound, (await mgmt.UpsertOrganisationAsync(_otherAppId, corpId, new OrganisationUpsert("Hijack", "corporate", null, null))).Status);

        // Update keeps the type, parent and path.
        ManagementResult<OrganisationDto> renamed = await mgmt.UpsertOrganisationAsync(_appId, hospitalId, new OrganisationUpsert("Apulki MC", "hospital", null, null));
        Assert.Equal("Apulki MC", renamed.Value!.Name);
        Assert.Equal(corpId, renamed.Value.ParentId);
        Assert.Equal(hospital.Value.Path, renamed.Value.Path);
    }

    [PostgresFact]
    public async Task Membership_GrantsAppGrant_ChangesRole_AndRevokes()
    {
        EfManagementService mgmt = Create(out SangamDbContext db);
        Guid orgId = Guid.NewGuid();
        await mgmt.UpsertRoleAsync(_appId, "doctor", Doctor);
        await mgmt.UpsertRoleAsync(_appId, "nurse", Doctor with { DisplayName = "Nurse", Permissions = ["patient:read"] });
        await mgmt.UpsertOrganisationAsync(_appId, orgId, new OrganisationUpsert("Apulki", "hospital", null, null));

        ManagementResult<MembershipDto> granted = await mgmt.UpsertMembershipAsync(_appId, orgId, _userId, new MembershipUpsert("doctor", true));
        Assert.Equal(ManagementStatus.Ok, granted.Status);
        Assert.True(granted.Value!.AppliesToDescendants);
        Assert.True(await db.AppGrants.AnyAsync(g => g.UserId == _userId && g.AppId == _appId && g.RevokedAt == null));

        ManagementResult<MembershipDto> changed = await mgmt.UpsertMembershipAsync(_appId, orgId, _userId, new MembershipUpsert("nurse", false));
        Assert.Equal("nurse", changed.Value!.Role);
        Assert.Single(await mgmt.ListMembersAsync(_appId, orgId));

        Assert.Equal(ManagementStatus.NotFound, (await mgmt.UpsertMembershipAsync(_appId, orgId, _userId, new MembershipUpsert("ghost", false))).Status);
        Assert.Equal(ManagementStatus.NotFound, (await mgmt.UpsertMembershipAsync(_appId, orgId, Guid.NewGuid(), new MembershipUpsert("nurse", false))).Status);
        Assert.Equal(ManagementStatus.NotFound, (await mgmt.UpsertMembershipAsync(_otherAppId, orgId, _userId, new MembershipUpsert("nurse", false))).Status);

        Assert.Equal(ManagementStatus.Ok, (await mgmt.RevokeMembershipAsync(_appId, orgId, _userId)).Status);
        Assert.Empty(await mgmt.ListMembersAsync(_appId, orgId));
        Assert.Equal(ManagementStatus.NotFound, (await mgmt.RevokeMembershipAsync(_appId, orgId, _userId)).Status);
    }

    [PostgresFact]
    public async Task OrgScopedRole_WinsOverTheAppWideRoleOfTheSameCode()
    {
        EfManagementService mgmt = Create(out SangamDbContext db);
        Guid orgId = Guid.NewGuid();
        await mgmt.UpsertOrganisationAsync(_appId, orgId, new OrganisationUpsert("Apulki", "hospital", null, null));
        await mgmt.UpsertRoleAsync(_appId, "doctor", Doctor);
        await mgmt.UpsertRoleAsync(_appId, "doctor", Doctor with { DisplayName = "Senior consultant", Permissions = ["patient:read", "rx:write", "discharge:approve"], OrgId = orgId });

        await mgmt.UpsertMembershipAsync(_appId, orgId, _userId, new MembershipUpsert("doctor", false));

        EfTenancyQuery tenancy = new(db);
        IReadOnlyList<OrgClaim> claims = await tenancy.GetOrgClaimsAsync(_userId, _appId);
        Assert.Single(claims);
        Assert.Equal(["discharge:approve", "patient:read", "rx:write"], claims[0].Permissions);
    }

    [PostgresFact]
    public async Task ManagementWrites_AreAuditedAsApiActor()
    {
        EfManagementService mgmt = Create(out SangamDbContext db);
        await mgmt.UpsertRoleAsync(_appId, "doctor", Doctor);

        List<AuditEvent> events = await db.AuditEvents.Where(e => e.Action == "role.upsert").ToListAsync();
        Assert.Single(events);
        Assert.Equal(AuditActorType.Api, events[0].ActorType);
        Assert.Equal(_appId, events[0].ActorAppId);
        Assert.Null(events[0].ActorUserId);
    }

    private EfManagementService Create(out SangamDbContext db)
    {
        db = _pg.CreateContext();
        _contexts.Add(db);
        return new EfManagementService(db, new UtcClock(), new DirectAuditWriter(_pg));
    }

    private sealed class UtcClock : IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    }

    private sealed class DirectAuditWriter : IAuditWriter
    {
        private readonly PostgresFixture _pg;

        public DirectAuditWriter(PostgresFixture pg)
        {
            _pg = pg;
        }

        public async Task WriteAsync(AuditEntry entry, CancellationToken cancellationToken = default)
        {
            await using SangamDbContext db = _pg.CreateContext();
            db.AuditEvents.Add(new AuditEvent
            {
                Action = entry.Action,
                ActorType = entry.ActorType,
                ActorUserId = entry.ActorUserId,
                ActorAppId = entry.ActorAppId,
                TargetType = entry.TargetType,
                TargetId = entry.TargetId,
                Metadata = entry.Metadata,
                OccurredAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync(cancellationToken);
        }
    }
}
