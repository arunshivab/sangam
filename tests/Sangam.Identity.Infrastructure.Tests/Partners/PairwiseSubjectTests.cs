using Microsoft.EntityFrameworkCore;
using Sangam.Identity.Domain.Entities;
using Sangam.Identity.Infrastructure.Apps;
using Sangam.Identity.Infrastructure.Persistence;
using Sangam.Identity.Infrastructure.Tests.Accounts;
using Sangam.Identity.Infrastructure.Tests.Postgres;

namespace Sangam.Identity.Infrastructure.Tests.Partners;

/// <summary>
/// rc.14 (decision record 0017): every application knows a person by its own id, unrelated to their Sangam id and to every
/// other application's; it never changes; Sangam's own applications use the Sangam id; the ids go with the account.
/// </summary>
[Collection("postgres")]
public sealed class PairwiseSubjectTests : IAsyncLifetime
{
    private readonly PostgresFixture _pg;
    private Guid _his;
    private Guid _lab;
    private Guid _portal;
    private Guid _asha;
    private Guid _ravi;

    public PairwiseSubjectTests(PostgresFixture pg)
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
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await using SangamDbContext db = _pg.CreateContext();
        SangamUser asha = TestUsers.New("asha@example.in", "+919000000401");
        SangamUser ravi = TestUsers.New("ravi@example.in", "+919000000402");
        db.Users.AddRange(asha, ravi);
        App his = new() { Id = Guid.NewGuid(), ClientId = "lipi-his", Slug = "lipi-his", DisplayName = "LiPi HIS", OwnerCompanyName = "imagiQa", CreatedAt = now, UpdatedAt = now };
        App lab = new() { Id = Guid.NewGuid(), ClientId = "lab", Slug = "lab", DisplayName = "Lab", OwnerCompanyName = "Someone else", CreatedAt = now, UpdatedAt = now };
        App portal = new() { Id = Guid.NewGuid(), ClientId = "portal", Slug = "portal", DisplayName = "Portal", OwnerCompanyName = "Sangam", IsPlatform = true, CreatedAt = now, UpdatedAt = now };
        db.Apps.AddRange(his, lab, portal);
        await db.SaveChangesAsync();
        (_his, _lab, _portal, _asha, _ravi) = (his.Id, lab.Id, portal.Id, asha.Id, ravi.Id);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [PostgresFact]
    public async Task EachApplication_GetsItsOwnId_AndKeepsIt()
    {
        await using SangamDbContext db = _pg.CreateContext();
        Guid inHis = await PairwiseSubjects.ForAsync(db, _his, _asha);
        Guid inLab = await PairwiseSubjects.ForAsync(db, _lab, _asha);

        Assert.NotEqual(_asha, inHis);
        Assert.NotEqual(_asha, inLab);
        Assert.NotEqual(inHis, inLab);

        // The same id every time, for one person or many at once.
        Assert.Equal(inHis, await PairwiseSubjects.ForAsync(db, _his, _asha));
        IReadOnlyDictionary<Guid, Guid> both = await PairwiseSubjects.ForManyAsync(db, _his, [_asha, _ravi, _asha]);
        Assert.Equal(2, both.Count);
        Assert.Equal(inHis, both[_asha]);
        Assert.NotEqual(both[_asha], both[_ravi]);
    }

    [PostgresFact]
    public async Task AnId_NamesItsPerson_OnlyInItsOwnApplication()
    {
        await using SangamDbContext db = _pg.CreateContext();
        Guid inHis = await PairwiseSubjects.ForAsync(db, _his, _asha);

        Assert.Equal(_asha, await PairwiseSubjects.UserOfAsync(db, _his, inHis));
        Assert.Null(await PairwiseSubjects.UserOfAsync(db, _lab, inHis));
        Assert.Null(await PairwiseSubjects.UserOfAsync(db, _his, _asha));
        Assert.Null(await PairwiseSubjects.UserOfAsync(db, _his, Guid.NewGuid()));

        IReadOnlyDictionary<Guid, Guid> back = await PairwiseSubjects.UsersOfAsync(db, _his, [inHis, _asha]);
        Assert.Equal(_asha, Assert.Single(back).Value);
    }

    [PostgresFact]
    public async Task SangamsOwnApplications_UseTheSangamId_AndKeepNoRows()
    {
        await using SangamDbContext db = _pg.CreateContext();
        Assert.Equal(_asha, await PairwiseSubjects.ForAsync(db, _portal, _asha));
        Assert.Equal(_asha, await PairwiseSubjects.UserOfAsync(db, _portal, _asha));
        Assert.False(await db.AppSubjects.AnyAsync(s => s.AppId == _portal));
    }

    [PostgresFact]
    public async Task ManyRequestsAtOnce_AgreeOnOneId()
    {
        Task<Guid>[] racing = [.. Enumerable.Range(0, 8).Select(async _ =>
        {
            await using SangamDbContext db = _pg.CreateContext();
            return await PairwiseSubjects.ForAsync(db, _lab, _ravi);
        })];
        Guid[] ids = await Task.WhenAll(racing);
        Assert.Single(ids.Distinct());

        await using SangamDbContext check = _pg.CreateContext();
        Assert.Equal(1, await check.AppSubjects.CountAsync(s => s.AppId == _lab && s.UserId == _ravi));
    }

    [PostgresFact]
    public async Task TheIds_GoWithTheAccount()
    {
        await using (SangamDbContext db = _pg.CreateContext())
        {
            await PairwiseSubjects.ForAsync(db, _his, _ravi);
            await PairwiseSubjects.ForAsync(db, _lab, _ravi);
            await db.Users.Where(u => u.Id == _ravi).ExecuteDeleteAsync();
        }

        await using SangamDbContext check = _pg.CreateContext();
        Assert.False(await check.AppSubjects.AnyAsync(s => s.UserId == _ravi));
    }
}
