using Microsoft.EntityFrameworkCore;
using Sangam.Identity.Domain.Entities;
using Sangam.Identity.Domain.Enums;
using Sangam.Identity.Infrastructure.Persistence;
using Sangam.Identity.Infrastructure.Tests.Postgres;

namespace Sangam.Identity.Infrastructure.Tests.Persistence;

/// <summary>The rules the schema must enforce, proven against a real PostgreSQL.</summary>
[Collection("postgres")]
public sealed class SchemaTests : IAsyncLifetime
{
    private readonly PostgresFixture _pg;

    public SchemaTests(PostgresFixture pg)
    {
        _pg = pg;
    }

    public Task InitializeAsync() => _pg.IsAvailable ? _pg.ResetAsync() : Task.CompletedTask;

    public Task DisposeAsync() => Task.CompletedTask;

    [PostgresFact]
    public async Task OrgTypes_AreSeededByMigration()
    {
        await using SangamDbContext db = _pg.CreateContext();

        List<OrgType> types = await db.OrgTypes.OrderBy(t => t.SortOrder).ToListAsync();

        Assert.Contains(types, t => t.Code == "corporate" && t.CanBeRoot);
        Assert.Contains(types, t => t.Code == "department" && !t.CanBeRoot && !t.CanHaveChildren);
        Assert.Equal(8, types.Count);
    }

    [PostgresFact]
    public async Task AuditEvents_CannotBeUpdatedOrDeleted()
    {
        await using SangamDbContext db = _pg.CreateContext();
        db.AuditEvents.Add(new AuditEvent { Action = "user.login.success", ActorType = AuditActorType.User, OccurredAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();

        int updated = await db.Database.ExecuteSqlRawAsync("UPDATE audit_events SET action = 'tampered'");
        int deleted = await db.Database.ExecuteSqlRawAsync("DELETE FROM audit_events");
        List<string> actions = await db.AuditEvents.AsNoTracking().Select(e => e.Action).ToListAsync();

        Assert.Equal(0, updated);
        Assert.Equal(0, deleted);
        Assert.Equal(["user.login.success"], actions);
    }

    [PostgresFact]
    public async Task OrgMembership_CanBeRegrantedAfterRevocation_ButNotDuplicatedWhileActive()
    {
        await using SangamDbContext db = _pg.CreateContext();
        (SangamUser user, App app, Organisation org, Role role) = await SeedGraphAsync(db);

        OrgMembership first = Membership(user, org, app, role);
        db.OrgMemberships.Add(first);
        await db.SaveChangesAsync();

        db.OrgMemberships.Add(Membership(user, org, app, role));
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();

        OrgMembership stored = await db.OrgMemberships.SingleAsync(m => m.Id == first.Id);
        stored.RevokedAt = DateTimeOffset.UtcNow;
        db.OrgMemberships.Add(Membership(user, org, app, role));
        await db.SaveChangesAsync();

        Assert.Equal(2, await db.OrgMemberships.CountAsync(m => m.UserId == user.Id));
        Assert.Equal(1, await db.OrgMemberships.CountAsync(m => m.UserId == user.Id && m.RevokedAt == null));
    }

    [PostgresFact]
    public async Task Roles_AreUniquePerAppAndOrgScope_TreatingNullOrgAsOneScope()
    {
        await using SangamDbContext db = _pg.CreateContext();
        (_, App app, Organisation org, _) = await SeedGraphAsync(db);

        db.Roles.Add(new Role { Id = Guid.NewGuid(), AppId = app.Id, Code = "doctor", DisplayName = "Doctor", CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();

        // Same code, app-wide again: rejected even though org_id is NULL on both rows.
        db.Roles.Add(new Role { Id = Guid.NewGuid(), AppId = app.Id, Code = "doctor", DisplayName = "Doctor again", CreatedAt = DateTimeOffset.UtcNow });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();

        // Same code, scoped to one organisation: allowed (an org-specific override the app created).
        db.Roles.Add(new Role { Id = Guid.NewGuid(), AppId = app.Id, OrgId = org.Id, Code = "doctor", DisplayName = "Doctor (org)", CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();

        Assert.Equal(2, await db.Roles.CountAsync(r => r.Code == "doctor"));
    }

    [PostgresFact]
    public async Task Organisation_PathPrefixQuery_ReturnsWholeSubtree()
    {
        await using SangamDbContext db = _pg.CreateContext();
        (_, App app, Organisation corp, _) = await SeedGraphAsync(db);

        Organisation hospital = Child(corp, "hospital", "Apulki Medical Center", app);
        Organisation department = Child(hospital, "department", "Oncology", app);
        Organisation otherCorp = new() { Id = Guid.NewGuid(), Name = "Other Group", OrgTypeCode = "corporate", RegisteredViaAppId = app.Id, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
        otherCorp.Path = OrganisationPath.ForRoot(otherCorp.Id);
        db.Organisations.AddRange(hospital, department, otherCorp);
        await db.SaveChangesAsync();

        List<string> subtree = await db.Organisations
            .Where(o => o.Path.StartsWith(corp.Path))
            .OrderBy(o => o.Depth)
            .Select(o => o.Name)
            .ToListAsync();

        Assert.Equal([corp.Name, "Apulki Medical Center", "Oncology"], subtree);
    }

    [PostgresFact]
    public async Task Organisation_DepthCheck_RejectsRootWithNonZeroDepth()
    {
        await using SangamDbContext db = _pg.CreateContext();
        (_, App app, _, _) = await SeedGraphAsync(db);

        Organisation bad = new() { Id = Guid.NewGuid(), Name = "Bad", OrgTypeCode = "clinic", Depth = 2, RegisteredViaAppId = app.Id, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
        bad.Path = OrganisationPath.ForRoot(bad.Id);
        db.Organisations.Add(bad);

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [PostgresFact]
    public async Task Users_MobileIsUniqueAmongLiveUsers()
    {
        await using SangamDbContext db = _pg.CreateContext();
        db.Users.Add(NewUser("a@example.in", "+919876543210"));
        await db.SaveChangesAsync();

        db.Users.Add(NewUser("b@example.in", "+919876543210"));
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();

        SangamUser gone = NewUser("c@example.in", "+919876543210");
        gone.Status = UserStatus.DeletedHard;
        db.Users.Add(gone);
        await db.SaveChangesAsync();

        Assert.Equal(2, await db.Users.CountAsync(u => u.PhoneNumber == "+919876543210"));
    }

    private static SangamUser NewUser(string email, string? mobile)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        return new SangamUser
        {
            Id = Guid.NewGuid(),
            UserName = email,
            NormalizedUserName = email.ToUpperInvariant(),
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            Name = email,
            PhoneNumber = mobile,
            SecurityStamp = Guid.NewGuid().ToString("N"),
            CreatedAt = now,
            UpdatedAt = now,
            LastPasswordChangeAt = now,
        };
    }

    private static OrgMembership Membership(SangamUser user, Organisation org, App app, Role role) => new()
    {
        Id = Guid.NewGuid(),
        UserId = user.Id,
        OrgId = org.Id,
        AppId = app.Id,
        RoleId = role.Id,
        GrantedAt = DateTimeOffset.UtcNow,
    };

    private static Organisation Child(Organisation parent, string type, string name, App app)
    {
        Organisation child = new()
        {
            Id = Guid.NewGuid(),
            Name = name,
            OrgTypeCode = type,
            ParentOrgId = parent.Id,
            Depth = parent.Depth + 1,
            RegisteredViaAppId = app.Id,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        child.Path = OrganisationPath.ForChild(parent.Path, child.Id);
        return child;
    }

    private static async Task<(SangamUser User, App App, Organisation Org, Role Role)> SeedGraphAsync(SangamDbContext db)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        SangamUser user = NewUser("rajesh@example.in", null);
        App app = new() { Id = Guid.NewGuid(), ClientId = "test-app", Slug = "test-app", DisplayName = "Test App", OwnerCompanyName = "Test Co", CreatedAt = now, UpdatedAt = now };
        Organisation corp = new() { Id = Guid.NewGuid(), Name = "Apulki Group", OrgTypeCode = "corporate", RegisteredViaAppId = app.Id, CreatedAt = now, UpdatedAt = now };
        corp.Path = OrganisationPath.ForRoot(corp.Id);
        Role role = new() { Id = Guid.NewGuid(), AppId = app.Id, Code = "org_admin", DisplayName = "Organisation admin", IsSystem = true, CreatedAt = now };

        db.Users.Add(user);
        db.Apps.Add(app);
        db.Organisations.Add(corp);
        db.Roles.Add(role);
        await db.SaveChangesAsync();
        return (user, app, corp, role);
    }
}
