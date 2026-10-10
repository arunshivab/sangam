using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Sangam.Identity.Application.Portal;
using Sangam.Identity.Application.Tenancy;
using Sangam.Identity.Domain;
using Sangam.Identity.Domain.Entities;
using Sangam.Identity.Domain.Enums;
using Sangam.Identity.Infrastructure.Maintenance;
using Sangam.Identity.Infrastructure.Persistence;
using Sangam.Identity.Infrastructure.Services;
using Sangam.Identity.Infrastructure.Tests.Accounts;
using Sangam.Identity.Infrastructure.Tests.Postgres;

namespace Sangam.Identity.Infrastructure.Tests.Maintenance;

/// <summary>
/// rc.6 (SGM-910 section 7): an application's inactivity limit ends unused connections after 30 days' notice; an account
/// with no partner connection and no sign-in for three years is told, then deleted; any use clears the notice.
/// </summary>
[Collection("postgres")]
public sealed class InactivityTests : IAsyncLifetime
{
    private readonly PostgresFixture _pg;
    private ServiceProvider _provider = null!;
    private Guid _app;
    private Guid _org;

    public InactivityTests(PostgresFixture pg)
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
        }).Build();
        ServiceCollection services = new();
        services.AddLogging();
        services.AddDataProtection();
        services.AddSingleton(configuration);
        services.AddSangamInfrastructure(configuration);
        _provider = services.BuildServiceProvider();

        await using SangamDbContext db = _pg.CreateContext();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        App app = new() { Id = Guid.NewGuid(), ClientId = "shop", Slug = "shop", DisplayName = "Big Shop", OwnerCompanyName = "Shop Pvt Ltd", CreatedAt = now, UpdatedAt = now, InactivityLimitYears = 3 };
        db.Apps.Add(app);
        await db.SaveChangesAsync();
        (_app, _org) = (app.Id, Guid.NewGuid());
        using IServiceScope scope = _provider.CreateScope();
        IManagementService management = scope.ServiceProvider.GetRequiredService<IManagementService>();
        await management.UpsertRoleAsync(_app, "buyer", new RoleUpsert("Buyer", null, ["orders:read"], null), ManagementActor.Api);
        await management.UpsertOrganisationAsync(_app, _org, new OrganisationUpsert("Shop", "store", null, null), ManagementActor.Api);
    }

    public async Task DisposeAsync()
    {
        if (_provider is not null)
        {
            await _provider.DisposeAsync();
        }
    }

    [PostgresFact]
    public async Task AnUnusedConnection_IsToldThirtyDaysAhead_ThenEnds_WithItsRolesAttributesConsentAndTokens()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        Guid soon = await PersonAsync("soon@shop.test", now.AddYears(-3).AddDays(10));
        Guid gone = await PersonAsync("gone@shop.test", now.AddYears(-4), noticeAt: now.AddDays(-31));
        Guid recent = await PersonAsync("recent@shop.test", now.AddYears(-3).AddDays(40));
        Guid toldLately = await PersonAsync("told@shop.test", now.AddYears(-4), noticeAt: now.AddDays(-5));

        (int notices, int ended, int accounts) = await RunAsync();
        Assert.Equal((1, 1, 0), (notices, ended, accounts));

        await using SangamDbContext db = _pg.CreateContext();
        Assert.NotNull((await Grant(db, soon)).InactivityNoticeAt);
        Assert.Null((await Grant(db, recent)).InactivityNoticeAt);
        Assert.Null((await Grant(db, toldLately)).RevokedAt);
        Assert.Equal("Your connection to Big Shop will end", Outbox().LatestFor("soon@shop.test")!.Message.Subject);

        AppGrant expired = await db.AppGrants.SingleAsync(g => g.UserId == gone);
        Assert.NotNull(expired.RevokedAt);
        Assert.False(await db.OrgMemberships.AnyAsync(m => m.UserId == gone && m.RevokedAt == null));
        Assert.False(await db.UserAttributeValues.AnyAsync(v => v.UserId == gone));
        Assert.False(await db.Consents.AnyAsync(c => c.UserId == gone && c.RevokedAt == null));
        Assert.True(await db.AuditEvents.AnyAsync(e => e.Action == AuditActions.ConsentInactivityExpire && e.TargetId == gone && e.ActorAppId == _app));
        Assert.Equal("Your connection to Big Shop has ended", Outbox().LatestFor("gone@shop.test")!.Message.Subject);
    }

    [PostgresFact]
    public async Task SigningInToTheApplication_ClearsTheNotice_AndKeepsTheConnection()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        Guid person = await PersonAsync("back@shop.test", now.AddYears(-4), noticeAt: now.AddDays(-31));
        using (IServiceScope scope = _provider.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<ISessionService>().RecordUseAsync(person, _app);
        }

        Assert.Equal((0, 0, 0), await RunAsync());
        await using SangamDbContext db = _pg.CreateContext();
        AppGrant grant = await Grant(db, person);
        Assert.Null(grant.InactivityNoticeAt);
        Assert.Null(grant.RevokedAt);
        Assert.True(grant.LastUsedAt > now.AddMinutes(-1));
    }

    [PostgresFact]
    public async Task AnAccountWithNoConnection_AndNoSignInForThreeYears_IsTold_ThenDeleted_ButNeverAnOperatorOrAnAdministrator()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await using (SangamDbContext db = _pg.CreateContext())
        {
            SangamUser told = Old("tell@idle.test", now.AddYears(-3).AddDays(5), null);
            SangamUser due = Old("due@idle.test", now.AddYears(-3).AddDays(-5), now.AddDays(-31));
            SangamUser op = Old("operator@idle.test", now.AddYears(-5), now.AddDays(-60));
            SangamUser admin = Old("admin@idle.test", now.AddYears(-5), now.AddDays(-60));
            SangamUser connected = Old("connected@idle.test", now.AddYears(-5), null);
            db.Users.AddRange(told, due, op, admin, connected);
            db.PlatformOperators.Add(new PlatformOperator { Id = Guid.NewGuid(), UserId = op.Id, Role = PlatformRole.Viewer, GrantedAt = now });
            db.AppAdmins.Add(new AppAdmin { Id = Guid.NewGuid(), AppId = _app, UserId = admin.Id, Role = AppAdminRole.Owner, GrantedAt = now });
            db.AppGrants.Add(new AppGrant { Id = Guid.NewGuid(), AppId = _app, UserId = connected.Id, GrantedAt = now.AddYears(-5), LastUsedAt = now.AddYears(-2) });
            await db.SaveChangesAsync();
        }

        (int notices, int _, int accounts) = await RunAsync();
        Assert.Equal((1, 1), (notices, accounts));

        await using SangamDbContext check = _pg.CreateContext();
        Assert.NotNull((await check.Users.SingleAsync(u => u.Email == "tell@idle.test")).InactivityNoticeAt);
        Assert.Equal("Your Sangam account will be deleted", Outbox().LatestFor("tell@idle.test")!.Message.Subject);
        SangamUser deleted = await check.Users.SingleAsync(u => u.Email == "due@idle.test");
        Assert.Equal(UserStatus.DeletedSoft, deleted.Status);
        Assert.NotNull(deleted.PurgeAfter);
        Assert.True(await check.AuditEvents.AnyAsync(e => e.Action == AuditActions.UserAccountInactivityDeletion && e.TargetId == deleted.Id));
        foreach (string kept in new[] { "operator@idle.test", "admin@idle.test", "connected@idle.test" })
        {
            Assert.Equal(UserStatus.Active, (await check.Users.SingleAsync(u => u.Email == kept)).Status);
        }
    }

    private async Task<(int, int, int)> RunAsync()
    {
        using IServiceScope scope = _provider.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<InactivityService>().RunAsync();
    }

    private InMemoryEmailOutbox Outbox() => _provider.GetRequiredService<InMemoryEmailOutbox>();

    private static Task<AppGrant> Grant(SangamDbContext db, Guid userId) => db.AppGrants.SingleAsync(g => g.UserId == userId);

    private static SangamUser Old(string email, DateTimeOffset lastSignIn, DateTimeOffset? noticeAt)
    {
        SangamUser user = TestUsers.New(email, null);
        user.CreatedAt = lastSignIn.AddYears(-1);
        user.LastSignInAt = lastSignIn;
        user.InactivityNoticeAt = noticeAt;
        return user;
    }

    private async Task<Guid> PersonAsync(string email, DateTimeOffset lastUsed, DateTimeOffset? noticeAt = null)
    {
        Guid id;
        await using (SangamDbContext db = _pg.CreateContext())
        {
            SangamUser user = TestUsers.New(email, null);
            user.LastSignInAt = DateTimeOffset.UtcNow;
            db.Users.Add(user);
            db.AppGrants.Add(new AppGrant { Id = Guid.NewGuid(), AppId = _app, UserId = user.Id, GrantedAt = lastUsed.AddDays(-1), LastUsedAt = lastUsed, InactivityNoticeAt = noticeAt });
            db.Consents.Add(new Consent { Id = Guid.NewGuid(), UserId = user.Id, AppId = _app, Scope = "openid", ConsentVersion = "1", GrantedAt = lastUsed });
            UserAttributeDefinition definition = new() { Id = Guid.NewGuid(), AppId = _app, Key = "loyalty_" + user.Id.ToString("N")[..8], Label = "Loyalty", CreatedAt = lastUsed };
            db.UserAttributeDefinitions.Add(definition);
            db.UserAttributeValues.Add(new UserAttributeValue { DefinitionId = definition.Id, UserId = user.Id, Value = "gold", UpdatedAt = lastUsed });
            await db.SaveChangesAsync();
            id = user.Id;
        }

        using IServiceScope scope = _provider.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IManagementService>().UpsertMembershipAsync(_app, _org, id, new MembershipUpsert("buyer", false), ManagementActor.Api);
        return id;
    }
}
