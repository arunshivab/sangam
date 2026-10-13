using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Sangam.Identity.Application.Admin;
using Sangam.Identity.Domain;
using Sangam.Identity.Domain.Entities;
using Sangam.Identity.Domain.Enums;
using Sangam.Identity.Infrastructure.Persistence;
using Sangam.Identity.Infrastructure.Tests.Accounts;
using Sangam.Identity.Infrastructure.Tests.Postgres;

namespace Sangam.Identity.Infrastructure.Tests.Admin;

/// <summary>
/// rc.10 (O1, O2, O4, O5): the operator console's paged users list, its work queue, the owner's overview and a client
/// secret's age. The queue names accounts and clocks, never a DigiLocker record.
/// </summary>
[Collection("postgres")]
public sealed class ConsoleHomeTests : IAsyncLifetime
{
    private const string RecordName = "Record Name From DigiLocker";

    private readonly List<IServiceScope> _scopes = [];
    private readonly PostgresFixture _pg;
    private ServiceProvider _provider = null!;
    private Guid _owner;
    private Guid _support;
    private Guid _viewer;
    private Guid _appId;

    public ConsoleHomeTests(PostgresFixture pg)
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

        await using SangamDbContext db = _pg.CreateContext();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        SangamUser owner = TestUsers.New("owner@console.test", "+919876507001");
        SangamUser support = TestUsers.New("support@console.test", "+919876507002");
        SangamUser viewer = TestUsers.New("viewer@console.test", "+919876507003");
        db.Users.AddRange(owner, support, viewer);
        db.PlatformOperators.AddRange(
            new PlatformOperator { Id = Guid.NewGuid(), UserId = owner.Id, Role = PlatformRole.Owner, GrantedAt = now },
            new PlatformOperator { Id = Guid.NewGuid(), UserId = support.Id, Role = PlatformRole.Support, GrantedAt = now },
            new PlatformOperator { Id = Guid.NewGuid(), UserId = viewer.Id, Role = PlatformRole.Viewer, GrantedAt = now });
        App app = new() { Id = Guid.NewGuid(), ClientId = "his-console", Slug = "his-console", DisplayName = "Console HIS", OwnerCompanyName = "imagiQa", CreatedAt = now, UpdatedAt = now, SecretSetAt = now.AddDays(-400) };
        db.Apps.Add(app);
        await db.SaveChangesAsync();
        (_owner, _support, _viewer, _appId) = (owner.Id, support.Id, viewer.Id, app.Id);
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
    public async Task TheUsersList_Pages_AndFiltersByStatusAndOrganisation()
    {
        Guid member;
        await using (SangamDbContext db = _pg.CreateContext())
        {
            for (int i = 0; i < 30; i++)
            {
                db.Users.Add(TestUsers.New($"person{i:D2}@console.test", null));
            }

            SangamUser suspended = TestUsers.New("suspended@console.test", null);
            suspended.Status = UserStatus.Suspended;
            SangamUser nurse = TestUsers.New("nurse@console.test", null);
            nurse.TwoFactorEnabled = true;
            db.Users.AddRange(suspended, nurse);
            db.AppGrants.Add(new AppGrant { Id = Guid.NewGuid(), UserId = nurse.Id, AppId = _appId, GrantedAt = DateTimeOffset.UtcNow });
            Organisation org = Organisation(Guid.NewGuid(), "Apulki Medical Center", null, _appId);
            db.Organisations.Add(org);
            Role role = new() { Id = Guid.NewGuid(), AppId = _appId, Code = "ns", DisplayName = "Nurse", CreatedAt = DateTimeOffset.UtcNow };
            db.Roles.Add(role);
            db.OrgMemberships.Add(new OrgMembership { Id = Guid.NewGuid(), UserId = nurse.Id, OrgId = org.Id, AppId = _appId, RoleId = role.Id, GrantedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
            member = nurse.Id;
        }

        IAdminService admin = Service();
        AdminUserPage first = await admin.ListUsersAsync(_viewer, new AdminUserQuery(null, null, null, 1, 25));
        Assert.Equal(25, first.Rows.Count);
        Assert.Equal(35, first.Total);
        AdminUserPage second = await admin.ListUsersAsync(_viewer, new AdminUserQuery(null, null, null, 2, 25));
        Assert.Equal(10, second.Rows.Count);
        Assert.Empty(first.Rows.Select(r => r.Id).Intersect(second.Rows.Select(r => r.Id)));

        // A page past the end shows the last one.
        Assert.Equal(2, (await admin.ListUsersAsync(_viewer, new AdminUserQuery(null, null, null, 9, 25))).Page);

        AdminUserPage atApulki = await admin.ListUsersAsync(_viewer, new AdminUserQuery(null, null, "apulki", 1, 25));
        Assert.Equal(member, Assert.Single(atApulki.Rows).Id);
        AdminUserPage suspendedOnly = await admin.ListUsersAsync(_viewer, new AdminUserQuery("console.test", UserStatus.Suspended, null, 1, 25));
        Assert.Equal("suspended@console.test", Assert.Single(suspendedOnly.Rows).Email);

        // rc.11: by application used, and by whether an authenticator app is set up.
        Assert.Equal(member, Assert.Single((await admin.ListUsersAsync(_viewer, new AdminUserQuery(null, null, null, 1, 25, _appId))).Rows).Id);
        Assert.Equal(member, Assert.Single((await admin.ListUsersAsync(_viewer, new AdminUserQuery("console.test", null, null, 1, 25, null, true))).Rows).Id);
        Assert.Equal(34, (await admin.ListUsersAsync(_viewer, new AdminUserQuery("console.test", null, null, 1, 25, null, false))).Total);
    }

    [PostgresFact]
    public async Task TheAuditTrail_FiltersAndPages_IsForSupportAndOwners_AndRecordsEachSearch()
    {
        // rc.11: the operator's audit trail.
        await using (SangamDbContext db = _pg.CreateContext())
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            for (int i = 11; i >= 0; i--)
            {
                db.AuditEvents.Add(new AuditEvent { Action = i % 3 == 0 ? "user.login.fail" : "user.login.success", ActorType = AuditActorType.User, ActorUserId = _viewer, TargetType = "user", TargetId = _viewer, OccurredAt = now.AddMinutes(-i), IpAddress = "10.0.0." + i });
            }

            await db.SaveChangesAsync();
        }

        IAdminService admin = Service();
        Assert.Empty((await admin.AuditTrailAsync(_viewer, new AuditTrailQuery(null, null, null, null, null, false, 1, 50))).Rows);

        AuditTrailPage logins = await admin.AuditTrailAsync(_support, new AuditTrailQuery("user.login", null, "viewer@console.test", null, null, false, 1, 10));
        Assert.Equal(10, logins.Rows.Count);
        Assert.True(logins.HasMore);
        Assert.All(logins.Rows, r => Assert.StartsWith("user.login", r.Action, StringComparison.Ordinal));
        Assert.Equal("Test User", logins.Rows[0].About);
        Assert.True(logins.Rows[0].At >= logins.Rows[^1].At);

        AuditTrailPage failed = await admin.AuditTrailAsync(_owner, new AuditTrailQuery("user.login", AuditActorType.User, null, null, null, true, 1, 50));
        Assert.Equal(4, failed.Rows.Count);
        Assert.All(failed.Rows, r => Assert.True(r.Failed));

        await using SangamDbContext check = _pg.CreateContext();
        Assert.Equal(2, await check.AuditEvents.CountAsync(e => e.Action == AuditActions.AdminAuditTrailView));
        AuditEvent viewed = await check.AuditEvents.FirstAsync(e => e.Action == AuditActions.AdminAuditTrailView && e.ActorUserId == _support);
        Assert.DoesNotContain("viewer@console.test", viewed.Metadata, StringComparison.Ordinal);
    }

    [PostgresFact]
    public async Task BulkSignOut_IsForSupportAndOwners_NeedsAReason_AndAuditsEachPerson()
    {
        // rc.11 (O2): the Users page's one bulk action.
        DateTimeOffset now = DateTimeOffset.UtcNow;
        List<Guid> people = [];
        await using (SangamDbContext db = _pg.CreateContext())
        {
            for (int i = 0; i < 2; i++)
            {
                SangamUser p = TestUsers.New($"bulk{i}@console.test", null);
                db.Users.Add(p);
                people.Add(p.Id);
                db.UserSessions.Add(new UserSession { Id = Guid.NewGuid(), UserId = p.Id, CreatedAt = now, LastSeenAt = now });
            }

            db.UserSessions.Add(new UserSession { Id = Guid.NewGuid(), UserId = _support, CreatedAt = now, LastSeenAt = now });
            await db.SaveChangesAsync();
        }

        IAdminService admin = Service();
        Assert.False((await admin.ForceSignOutManyAsync(_viewer, people, "Shared laptop lost", null)).Succeeded);
        Assert.False((await admin.ForceSignOutManyAsync(_support, people, " ", null)).Succeeded);
        Assert.False((await admin.ForceSignOutManyAsync(_support, [_support], "Shared laptop lost", null)).Succeeded);
        Assert.False((await admin.ForceSignOutManyAsync(_support, [.. Enumerable.Range(0, 101).Select(_ => Guid.NewGuid())], "Too many", null)).Succeeded);

        AdminResult done = await admin.ForceSignOutManyAsync(_support, [.. people, _support], "Shared laptop lost", null);
        Assert.True(done.Succeeded, done.Message);

        await using SangamDbContext check = _pg.CreateContext();
        Assert.Equal(0, await check.UserSessions.CountAsync(s => people.Contains(s.UserId) && s.RevokedAt == null));
        Assert.Equal(1, await check.UserSessions.CountAsync(s => s.UserId == _support && s.RevokedAt == null));
        List<AuditEvent> audited = await check.AuditEvents.AsNoTracking().Where(e => e.Action == AuditActions.AdminUserForceLogout).ToListAsync();
        Assert.Equal(people.Order(), audited.Select(e => e.TargetId!.Value).Order());
        Assert.All(audited, e =>
        {
            Assert.Equal(_support, e.ActorUserId);
            Assert.Contains("Shared laptop lost", e.Metadata, StringComparison.Ordinal);
        });
    }

    [PostgresFact]
    public async Task TheWorkQueue_PutsDecisionsThenTheSoonestClockFirst_NamesNoRecord_AndShowsAViewerOnlyGrievances()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        Guid person;
        await using (SangamDbContext db = _pg.CreateContext())
        {
            SangamUser p = TestUsers.New("queue@console.test", null);
            p.FirstName = "Meera";
            p.LastName = "Pillai";
            db.Users.Add(p);
            person = p.Id;
            db.MfaResetRequests.AddRange(
                Reset(p.Id, now.AddHours(-30), now.AddHours(42), MfaResetReview.Waiting, privileged: true),
                Reset(p.Id, now.AddHours(-2), now.AddHours(22), null, privileged: false));
            db.Grievances.Add(new Grievance
            {
                Id = Guid.NewGuid(),
                Reference = "GRV-2026-0001",
                ReceivedAt = now.AddDays(-1),
                Channel = "email",
                Category = "access",
                ComplainantName = "Anita",
                ComplainantContact = "anita@example.in",
                Summary = "Asks why an app still sees her",
                Status = GrievanceStatus.Received,
                AcknowledgeBy = now.AddHours(30),
                ResolveBy = now.AddDays(29),
                CreatedByUserId = _support,
            });
            await db.SaveChangesAsync();
        }

        IReadOnlyList<WorkItem> queue = await Service().WorkQueueAsync(_support);
        // A recovery waiting for a decision comes first (someone is locked out until an operator decides), then the clocks.
        Assert.Equal([WorkKind.Recovery, WorkKind.TwoStepReset, WorkKind.Grievance], queue.Select(i => i.Kind));
        Assert.All(queue, i => Assert.DoesNotContain(RecordName, i.Subject, StringComparison.Ordinal));
        WorkItem recovery = queue.Single(i => i.Kind == WorkKind.Recovery);
        Assert.Equal("Meera Pillai", recovery.Subject);
        Assert.True(recovery.Privileged);
        Assert.Equal("/recoveries", recovery.Link);
        Assert.Equal($"/users/{person:D}", queue.Single(i => i.Kind == WorkKind.TwoStepReset).Link);

        WorkItem grievance = Assert.Single(await Service().WorkQueueAsync(_viewer));
        Assert.Equal(WorkKind.Grievance, grievance.Kind);
        Assert.Contains("GRV-2026-0001", grievance.Subject, StringComparison.Ordinal);

        // Counting the queue on every page writes no refusal for a lower rank.
        await using SangamDbContext check = _pg.CreateContext();
        Assert.False(await check.AuditEvents.AnyAsync(e => e.Action == AuditActions.AccessDenied && e.ActorUserId == _viewer));
    }

    [PostgresFact]
    public async Task TheOverview_IsAnOwnersOnly_AndListsWhatNeedsAttention_AndTheTrail()
    {
        IAdminService admin = Service();
        Assert.Null(await admin.OverviewAsync(_support));

        // A refusal (a viewer trying to disable an application) shows in the trail as failed.
        Assert.False((await admin.SetAppStatusAsync(_viewer, _appId, AppStatus.Disabled, null)).Succeeded);

        AdminOverview overview = (await Service().OverviewAsync(_owner))!;
        Assert.True(overview.People >= 3);
        Assert.Contains(overview.Attention, a => a.Kind == AttentionKind.AppWithoutOwner && a.Subject == "Console HIS");
        Assert.Contains(overview.Attention, a => a.Kind == AttentionKind.SecretOld && a.Subject == "Console HIS");
        TrailRow refused = Assert.Single(overview.Trail, t => t.Action == AuditActions.AccessDenied);
        Assert.True(refused.Failed);
        Assert.Equal("Test User", refused.Actor);
    }

    [PostgresFact]
    public async Task AClientSecret_IsShownByAge_AndFlaggedAfterAYear()
    {
        AdminAppRow row = Assert.Single(await Service().ListAppsAsync(_viewer), a => a.Id == _appId);
        Assert.NotNull(row.SecretSetAt);
        Assert.True(row.SecretIsOld(DateTimeOffset.UtcNow));
        Assert.False((row with { SecretSetAt = DateTimeOffset.UtcNow.AddDays(-300) }).SecretIsOld(DateTimeOffset.UtcNow));
        Assert.Null(row.HasSecret);
    }

    private static Organisation Organisation(Guid id, string name, Organisation? parent, Guid appId) => new()
    {
        Id = id,
        Name = name,
        RegisteredViaAppId = appId,
        OrgTypeCode = "hospital",
        ParentOrgId = parent?.Id,
        Path = parent is null ? OrganisationPath.ForRoot(id) : OrganisationPath.ForChild(parent.Path, id),
        Depth = parent is null ? 0 : parent.Depth + 1,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    private static MfaResetRequest Reset(Guid userId, DateTimeOffset requested, DateTimeOffset effective, string? review, bool privileged) => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        RequestedByUserId = userId,
        VerificationMethod = "digilocker",
        Reference = "REC-" + Guid.NewGuid().ToString("N")[..8],
        Privileged = privileged,
        RequestedAt = requested,
        EffectiveAt = effective,
        CancelTokenHash = Guid.NewGuid().ToString("N"),
        ReviewStatus = review,
        Record = review is null ? null : "{\"name\":\"" + RecordName + "\"}",
    };

    private IAdminService Service()
    {
        IServiceScope scope = _provider.CreateScope();
        _scopes.Add(scope);
        return scope.ServiceProvider.GetRequiredService<IAdminService>();
    }
}
