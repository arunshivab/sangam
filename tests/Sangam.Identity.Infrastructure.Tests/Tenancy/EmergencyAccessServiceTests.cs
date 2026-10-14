using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Sangam.Identity.Application.Tenancy;
using Sangam.Identity.Domain.Entities;
using Sangam.Identity.Infrastructure.Persistence;
using Sangam.Identity.Infrastructure.Services;
using Sangam.Identity.Infrastructure.Tenancy;
using Sangam.Identity.Infrastructure.Tests.Accounts;
using Sangam.Identity.Infrastructure.Tests.Postgres;

namespace Sangam.Identity.Infrastructure.Tests.Tenancy;

/// <summary>
/// rc.12 (M12): emergency access. Only the hospital's staff, only where switched on (here or above), three a day, a reason
/// and a case reference; reviewers are e-mailed; only a reviewer judges, never their own use, and "not justified" needs a
/// note; admins see the log; the overview counts the week's uses.
/// </summary>
[Collection("postgres")]
public sealed class EmergencyAccessServiceTests : IAsyncLifetime
{
    private readonly List<IServiceScope> _scopes = [];
    private readonly PostgresFixture _pg;
    private ServiceProvider _provider = null!;
    private Guid _lipi;
    private Guid _apulki;
    private Guid _icu;
    private Guid _admin;
    private Guid _nurse;
    private Guid _reviewer;
    private Guid _outsider;

    public EmergencyAccessServiceTests(PostgresFixture pg)
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
        App lipi = new() { Id = Guid.NewGuid(), ClientId = "lipi-ea", Slug = "lipi-ea", DisplayName = "LiPi", OwnerCompanyName = "imagiQa", CreatedAt = now, UpdatedAt = now };
        db.Apps.Add(lipi);
        Guid rootId = Guid.NewGuid();
        Guid icuId = Guid.NewGuid();
        Organisation apulki = new() { Id = rootId, Name = "Apulki", OrgTypeCode = "hospital", Path = OrganisationPath.ForRoot(rootId), RegisteredViaAppId = lipi.Id, CreatedAt = now, UpdatedAt = now };
        db.Organisations.AddRange(apulki, new Organisation { Id = icuId, Name = "ICU", OrgTypeCode = "hospital", ParentOrgId = rootId, Path = OrganisationPath.ForChild(apulki.Path, icuId), Depth = 1, RegisteredViaAppId = lipi.Id, CreatedAt = now, UpdatedAt = now });
        SangamUser admin = TestUsers.New("bala@apulki.test", null);
        SangamUser nurse = TestUsers.New("meera@apulki.test", null);
        nurse.FirstName = "Meera";
        SangamUser reviewer = TestUsers.New("ms@apulki.test", null);
        reviewer.FirstName = "Sunita";
        SangamUser outsider = TestUsers.New("visitor@example.test", null);
        db.Users.AddRange(admin, nurse, reviewer, outsider);
        db.OrgMemberships.AddRange(
            new OrgMembership { Id = Guid.NewGuid(), AppId = lipi.Id, OrgId = icuId, UserId = nurse.Id, GrantedAt = now },
            new OrgMembership { Id = Guid.NewGuid(), AppId = lipi.Id, OrgId = rootId, UserId = reviewer.Id, GrantedAt = now },
            new OrgMembership { Id = Guid.NewGuid(), AppId = lipi.Id, OrgId = rootId, UserId = admin.Id, GrantedAt = now });
        db.OrgAdmins.AddRange(
            new OrgAdmin { Id = Guid.NewGuid(), AppId = lipi.Id, OrgId = rootId, UserId = admin.Id, Right = OrgAdminRight.Admin, GrantedAt = now },
            new OrgAdmin { Id = Guid.NewGuid(), AppId = lipi.Id, OrgId = rootId, UserId = reviewer.Id, Right = OrgAdminRight.EmergencyReviewer, GrantedAt = now });
        await db.SaveChangesAsync();
        (_lipi, _apulki, _icu, _admin, _nurse, _reviewer, _outsider) = (lipi.Id, rootId, icuId, admin.Id, nurse.Id, reviewer.Id, outsider.Id);
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
    [InlineData("LIPI-48213", true)]
    [InlineData("case/2026:17#3", true)]
    [InlineData("", false)]
    [InlineData("Ramesh Kumar", false)]
    public void ACaseReference_IsShortAndOpaque(string caseRef, bool ok) => Assert.Equal(ok, EfEmergencyAccessService.IsCaseRef(caseRef));

    [Fact]
    public void UnreviewedUses_TurnOrangeAfterADay_AndRedAfterThree()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        Assert.Equal(EmergencyReviewAge.Fresh, IEmergencyAccessService.AgeOf(now.AddHours(-23), false, now));
        Assert.Equal(EmergencyReviewAge.Due, IEmergencyAccessService.AgeOf(now.AddHours(-25), false, now));
        Assert.Equal(EmergencyReviewAge.Late, IEmergencyAccessService.AgeOf(now.AddHours(-73), false, now));
        Assert.Equal(EmergencyReviewAge.Reviewed, IEmergencyAccessService.AgeOf(now.AddHours(-73), true, now));
    }

    [PostgresFact]
    public async Task OnlyStaff_OnlyWhereSwitchedOn_AndThreeADay()
    {
        Assert.Equal(EmergencyStartStatus.SwitchedOff, (await Emergency().StartAsync(Ask(_nurse, "LIPI-1"))).Status);
        await using (SangamDbContext db = _pg.CreateContext())
        {
            await db.Organisations.Where(o => o.Id == _apulki).ExecuteUpdateAsync(u => u.SetProperty(o => o.EmergencyAccess, true));
        }

        // On at the customer covers the ICU beneath it.
        Assert.True(await Emergency().IsOnAsync(_lipi, _icu));
        Assert.Equal(EmergencyStartStatus.NotStaff, (await Emergency().StartAsync(Ask(_outsider, "LIPI-1"))).Status);
        Assert.Equal(EmergencyStartStatus.CaseRequired, (await Emergency().StartAsync(Ask(_nurse, "Ramesh Kumar"))).Status);
        Assert.Equal(EmergencyStartStatus.ReasonRequired, (await Emergency().StartAsync(Ask(_nurse, "LIPI-1") with { Reason = " " })).Status);

        EmergencyStartResult first = await Emergency().StartAsync(Ask(_nurse, "LIPI-1"));
        Assert.Equal(EmergencyStartStatus.Started, first.Status);
        Assert.Equal(first.Grant!.StartedAt + TimeSpan.FromMinutes(60), first.Grant.EndsAt);
        Assert.Equal(first.Grant.Id, (await Emergency().ActiveAsync(_nurse, _lipi, null, "LIPI-1"))!.Id);
        Assert.Equal(EmergencyStartStatus.Started, (await Emergency().StartAsync(Ask(_nurse, "LIPI-2"))).Status);
        Assert.Equal(EmergencyStartStatus.Started, (await Emergency().StartAsync(Ask(_nurse, "LIPI-3"))).Status);
        EmergencyStartResult fourth = await Emergency().StartAsync(Ask(_nurse, "LIPI-4"));
        Assert.Equal((EmergencyStartStatus.DailyLimit, 3), (fourth.Status, fourth.UsedToday));

        // The reviewer is e-mailed each time; the person using it never reviews their own.
        InMemoryEmailOutbox outbox = _provider.GetRequiredService<InMemoryEmailOutbox>();
        Assert.Equal(3, outbox.Recent.Count(m => m.Message.ToEmail == "ms@apulki.test" && m.Message.Subject.Contains("Emergency access used", StringComparison.Ordinal)));

        // Ended early by the application: gone from tokens refreshed after.
        Assert.False(await Emergency().EndAsync(_nurse, _lipi, null, " "));
        Assert.True(await Emergency().EndAsync(_nurse, _lipi, null, "Care handed over"));
        Assert.Null(await Emergency().ActiveByIdAsync(first.Grant.Id, _nurse));

        using IServiceScope scope = _provider.CreateScope();
        OrgOverview overview = (await scope.ServiceProvider.GetRequiredService<IOrgConsoleService>().OverviewAsync(_admin, _lipi, _apulki))!;
        Assert.Equal(3, overview.EmergencyUses);
        Assert.Equal(3, overview.Decisions.Count(d => d.Kind == OrgDecisionKind.EmergencyUse));
    }

    [PostgresFact]
    public async Task OnlyAReviewerJudges_AndNotJustifiedNeedsANote()
    {
        await using (SangamDbContext db = _pg.CreateContext())
        {
            await db.Organisations.Where(o => o.Id == _icu).ExecuteUpdateAsync(u => u.SetProperty(o => o.EmergencyAccess, true));
        }

        Guid use = (await Emergency().StartAsync(Ask(_nurse, "LIPI-9"))).Grant!.Id;
        Assert.Null(await Emergency().LogAsync(_nurse, _lipi, _apulki));
        EmergencyUse seenByAdmin = Assert.Single((await Emergency().LogAsync(_admin, _lipi, _apulki))!);
        Assert.False(seenByAdmin.MayReview);
        Assert.True(Assert.Single((await Emergency().LogAsync(_reviewer, _lipi, _apulki))!).MayReview);

        Assert.False(await Emergency().ReviewAsync(_admin, _lipi, use, EmergencyVerdict.Justified, null));
        Assert.False(await Emergency().ReviewAsync(_nurse, _lipi, use, EmergencyVerdict.Justified, null));
        Assert.False(await Emergency().ReviewAsync(_reviewer, _lipi, use, EmergencyVerdict.NotJustified, " "));
        Assert.True(await Emergency().ReviewAsync(_reviewer, _lipi, use, EmergencyVerdict.NotJustified, "Treating doctor was on the ward"));
        EmergencyUse judged = Assert.Single((await Emergency().LogAsync(_admin, _lipi, _apulki))!);
        Assert.Equal(EmergencyVerdict.NotJustified, judged.Verdict);
        Assert.Equal("Sunita", judged.ReviewedBy?.Split(' ')[0]);
    }

    [PostgresFact]
    public async Task TheApplicationsAccessReview_CountsWhatWaits_ByAge()
    {
        await using (SangamDbContext db = _pg.CreateContext())
        {
            await db.Organisations.Where(o => o.Id == _apulki).ExecuteUpdateAsync(u => u.SetProperty(o => o.EmergencyAccess, true));
            db.AppAdmins.Add(new AppAdmin { Id = Guid.NewGuid(), AppId = _lipi, UserId = _outsider, Role = Sangam.Identity.Domain.Enums.AppAdminRole.Owner, GrantedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }

        Guid old = (await Emergency().StartAsync(Ask(_nurse, "LIPI-7"))).Grant!.Id;
        await Emergency().StartAsync(Ask(_nurse, "LIPI-8"));
        await using (SangamDbContext db = _pg.CreateContext())
        {
            DateTimeOffset longAgo = DateTimeOffset.UtcNow.AddHours(-80);
            await db.EmergencyAccesses.Where(e => e.Id == old).ExecuteUpdateAsync(u => u.SetProperty(e => e.StartedAt, longAgo));
        }

        Assert.Empty(await Emergency().WaitingAsync(_admin, _lipi));
        EmergencyWaiting waiting = Assert.Single(await Emergency().WaitingAsync(_outsider, _lipi));
        Assert.Equal(("Apulki", 1, 0, 1), (waiting.CustomerName, waiting.Fresh, waiting.Due, waiting.Late));
    }

    private EmergencyRequest Ask(Guid userId, string caseRef) => new(userId, _lipi, _icu, null, caseRef, "Patient deteriorating", "totp");

    private IEmergencyAccessService Emergency()
    {
        IServiceScope scope = _provider.CreateScope();
        _scopes.Add(scope);
        return scope.ServiceProvider.GetRequiredService<IEmergencyAccessService>();
    }
}
