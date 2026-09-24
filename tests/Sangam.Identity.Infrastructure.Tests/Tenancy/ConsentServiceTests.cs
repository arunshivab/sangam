using Microsoft.EntityFrameworkCore;
using Sangam.Identity.Application.Abstractions;
using Sangam.Identity.Domain.Entities;
using Sangam.Identity.Infrastructure.Consents;
using Sangam.Identity.Infrastructure.Persistence;
using Sangam.Identity.Infrastructure.Tests.Accounts;
using Sangam.Identity.Infrastructure.Tests.Postgres;

namespace Sangam.Identity.Infrastructure.Tests.Tenancy;

[Collection("postgres")]
public sealed class ConsentServiceTests : IAsyncLifetime
{
    private static readonly string[] Requested = ["openid", "profile", "email"];
    private readonly PostgresFixture _pg;
    private Guid _appId;
    private Guid _userId;

    public ConsentServiceTests(PostgresFixture pg)
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
        App app = new() { Id = Guid.NewGuid(), ClientId = "his", Slug = "his", DisplayName = "HIS", OwnerCompanyName = "LiPi", ConsentVersion = "v1", CreatedAt = now, UpdatedAt = now };
        SangamUser user = TestUsers.New("consent@example.in", "+919000000022");
        db.Apps.Add(app);
        db.Users.Add(user);
        await db.SaveChangesAsync();
        _appId = app.Id;
        _userId = user.Id;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [PostgresFact]
    public async Task Grant_RecordsConsentAndAppGrant_AndSatisfiesTheSameScopes()
    {
        await using SangamDbContext db = _pg.CreateContext();
        EfConsentService consents = new(db, new UtcClock(), new NullAudit());

        Assert.False(await consents.HasValidConsentAsync(_userId, _appId, Requested));
        await consents.GrantAsync(_userId, _appId, Requested, "203.0.113.4", "xunit");

        Assert.True(await consents.HasValidConsentAsync(_userId, _appId, Requested));
        Assert.True(await consents.HasValidConsentAsync(_userId, _appId, ["openid", "email"]));
        Assert.False(await consents.HasValidConsentAsync(_userId, _appId, ["openid", "orgs.read"]));
        Assert.True(await db.AppGrants.AnyAsync(g => g.UserId == _userId && g.AppId == _appId && g.RevokedAt == null));
    }

    [PostgresFact]
    public async Task Regrant_RevokesThePreviousRow_KeepingHistory()
    {
        await using SangamDbContext db = _pg.CreateContext();
        EfConsentService consents = new(db, new UtcClock(), new NullAudit());

        await consents.GrantAsync(_userId, _appId, Requested, null, null);
        await consents.GrantAsync(_userId, _appId, [.. Requested, "orgs.read"], null, null);

        List<Domain.Entities.Consent> rows = await db.Consents.AsNoTracking().OrderBy(c => c.GrantedAt).ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.NotNull(rows[0].RevokedAt);
        Assert.Null(rows[1].RevokedAt);
        Assert.True(await consents.HasValidConsentAsync(_userId, _appId, ["orgs.read"]));
    }

    [PostgresFact]
    public async Task ConsentVersionChange_ForcesReConsent()
    {
        await using SangamDbContext db = _pg.CreateContext();
        EfConsentService consents = new(db, new UtcClock(), new NullAudit());
        await consents.GrantAsync(_userId, _appId, Requested, null, null);
        Assert.True(await consents.HasValidConsentAsync(_userId, _appId, Requested));

        await db.Apps.Where(a => a.Id == _appId).ExecuteUpdateAsync(s => s.SetProperty(a => a.ConsentVersion, "v2"));
        db.ChangeTracker.Clear();

        Assert.False(await consents.HasValidConsentAsync(_userId, _appId, Requested));
    }

    private sealed class UtcClock : IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    }

    private sealed class NullAudit : IAuditWriter
    {
        public Task WriteAsync(AuditEntry entry, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
