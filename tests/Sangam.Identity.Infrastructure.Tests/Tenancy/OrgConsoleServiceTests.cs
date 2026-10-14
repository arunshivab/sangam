using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Sangam.Identity.Application.Partners;
using Sangam.Identity.Application.Tenancy;
using Sangam.Identity.Domain;
using Sangam.Identity.Domain.Entities;
using Sangam.Identity.Domain.Enums;
using Sangam.Identity.Infrastructure.Persistence;
using Sangam.Identity.Infrastructure.Services;
using Sangam.Identity.Infrastructure.Tests.Accounts;
using Sangam.Identity.Infrastructure.Tests.Postgres;

namespace Sangam.Identity.Infrastructure.Tests.Tenancy;

/// <summary>
/// rc.12 (G1, G2): the organisation console. A customer or branch admin sees only their trees, across applications; a leaver
/// ends every place at once; admin rights go to people who work there and end with their last place; a closed customer
/// ends everything under it.
/// </summary>
[Collection("postgres")]
public sealed class OrgConsoleServiceTests : IAsyncLifetime
{
    private readonly List<IServiceScope> _scopes = [];
    private readonly PostgresFixture _pg;
    private ServiceProvider _provider = null!;
    private Guid _lipi;
    private Guid _medimaze;
    private Guid _apulki;
    private Guid _pune;
    private Guid _apulkiMedimaze;
    private Guid _armoki;
    private Guid _customerAdmin;
    private Guid _branchAdmin;
    private Guid _appAdmin;
    private Guid _priya;

    public OrgConsoleServiceTests(PostgresFixture pg)
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
        App lipi = new() { Id = Guid.NewGuid(), ClientId = "lipi-org", Slug = "lipi-org", DisplayName = "LiPi", OwnerCompanyName = "imagiQa", CreatedAt = now, UpdatedAt = now };
        App medimaze = new() { Id = Guid.NewGuid(), ClientId = "medimaze-org", Slug = "medimaze-org", DisplayName = "Medimaze", OwnerCompanyName = "Medimaze", CreatedAt = now, UpdatedAt = now };
        db.Apps.AddRange(lipi, medimaze);
        Organisation apulki = Root(lipi.Id, "Apulki", now);
        Organisation pune = Child(apulki, "Apulki Pune", now);
        Organisation apulkiMedimaze = Root(medimaze.Id, "Apulki", now);
        Organisation armoki = Root(lipi.Id, "Armoki", now);
        db.Organisations.AddRange(apulki, pune, apulkiMedimaze, armoki);
        Role doctor = new() { Id = Guid.NewGuid(), AppId = lipi.Id, Code = "doctor", DisplayName = "Doctor", CreatedAt = now };
        db.Roles.Add(doctor);

        SangamUser customerAdmin = Person("lakshmi@apulki.test", "Lakshmi", "Raman", now);
        SangamUser branchAdmin = Person("bala@apulki.test", "Bala", "K", now);
        SangamUser appAdmin = Person("owner@lipi.test", "Owner", "LiPi", now);
        SangamUser priya = Person("priya@example.test", "Priya", "Sharma", now);
        db.Users.AddRange(customerAdmin, branchAdmin, appAdmin, priya);
        db.AppAdmins.Add(new AppAdmin { Id = Guid.NewGuid(), AppId = lipi.Id, UserId = appAdmin.Id, Role = AppAdminRole.Owner, GrantedAt = now });
        db.OrgAdmins.AddRange(
            new OrgAdmin { Id = Guid.NewGuid(), AppId = lipi.Id, OrgId = apulki.Id, UserId = customerAdmin.Id, Right = OrgAdminRight.Admin, GrantedAt = now },
            new OrgAdmin { Id = Guid.NewGuid(), AppId = medimaze.Id, OrgId = apulkiMedimaze.Id, UserId = customerAdmin.Id, Right = OrgAdminRight.Admin, GrantedAt = now },
            new OrgAdmin { Id = Guid.NewGuid(), AppId = lipi.Id, OrgId = pune.Id, UserId = branchAdmin.Id, Right = OrgAdminRight.Admin, GrantedAt = now });
        db.OrgMemberships.AddRange(
            Place(lipi.Id, apulki.Id, customerAdmin.Id, null, now),
            Place(medimaze.Id, apulkiMedimaze.Id, customerAdmin.Id, null, now),
            Place(lipi.Id, pune.Id, branchAdmin.Id, null, now),
            Place(lipi.Id, pune.Id, priya.Id, doctor.Id, now, now.AddDays(5)),
            Place(medimaze.Id, apulkiMedimaze.Id, priya.Id, null, now),
            Place(lipi.Id, armoki.Id, priya.Id, doctor.Id, now));
        await db.SaveChangesAsync();
        (_lipi, _medimaze, _apulki, _pune, _apulkiMedimaze, _armoki) = (lipi.Id, medimaze.Id, apulki.Id, pune.Id, apulkiMedimaze.Id, armoki.Id);
        (_customerAdmin, _branchAdmin, _appAdmin, _priya) = (customerAdmin.Id, branchAdmin.Id, appAdmin.Id, priya.Id);
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
    public async Task EachAdmin_SeesTheirOwnTrees_AcrossApplications_AndNothingElse()
    {
        IReadOnlyList<OrgScope> customer = await Console().ScopesAsync(_customerAdmin);
        Assert.Equal([("LiPi", _apulki), ("Medimaze", _apulkiMedimaze)], customer.Select(s => (s.AppName, s.OrgId)).Order());
        Assert.All(customer, s => Assert.True(s.IsCustomer && s.Admin && !s.ViaApplication));
        Assert.Equal(_medimaze, customer.Single(s => s.AppName == "Medimaze").AppId);

        OrgScope branch = Assert.Single(await Console().ScopesAsync(_branchAdmin));
        Assert.Equal(_pune, branch.OrgId);
        Assert.False(branch.IsCustomer);

        // The application's admin administers every customer of the application.
        IReadOnlyList<OrgScope> app = await Console().ScopesAsync(_appAdmin);
        Assert.Equal(new[] { _apulki, _armoki }.Order(), app.Select(s => s.OrgId).Order());
        Assert.All(app, s => Assert.True(s.ViaApplication));

        // A branch admin cannot open the customer above them; nobody opens Armoki but the application's admin.
        Assert.Null(await Console().OverviewAsync(_branchAdmin, _lipi, _apulki));
        Assert.Null(await Console().PeopleAsync(_customerAdmin, _lipi, _armoki));
        Assert.NotNull(await Console().PeopleAsync(_appAdmin, _lipi, _armoki));
    }

    [PostgresFact]
    public async Task TheOverview_CountsWhatNeedsAction()
    {
        OrgOverview overview = (await Console().OverviewAsync(_customerAdmin, _lipi, _apulki))!;
        Assert.Equal(3, overview.Staff);
        Assert.Equal(1, overview.RolesEnding);
        OrgDecision ending = Assert.Single(overview.Decisions);
        Assert.Equal((OrgDecisionKind.RoleEnding, "Priya Sharma", "Apulki Pune"), (ending.Kind, ending.Name, ending.OrgName));
        Assert.Contains("doctor", ending.Detail, StringComparison.Ordinal);

        IReadOnlyList<OrgNode> tree = (await Console().TreeAsync(_customerAdmin, _lipi, _apulki))!;
        Assert.Equal([("Apulki", 0, 1), ("Apulki Pune", 1, 2)], tree.Select(n => (n.Name, n.Depth, n.People)));
    }

    [PostgresFact]
    public async Task ALeaver_EndsEveryPlaceInTheAdminsTrees_OnOneDay_AndNoOtherCustomers()
    {
        OrgLeaver found = Assert.Single(await Console().FindAsync(_customerAdmin, "pri"));
        Assert.Equal(2, found.Places.Count);
        Assert.DoesNotContain(found.Places, p => p.OrgName == "Armoki");

        DateOnly lastDay = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(3));
        Assert.Equal(0, await Console().LeaverAsync(_customerAdmin, _priya, lastDay, "  "));
        Assert.Equal(2, await Console().LeaverAsync(_customerAdmin, _priya, lastDay, "Moved to Delhi"));

        await using SangamDbContext db = _pg.CreateContext();
        List<OrgMembership> places = await db.OrgMemberships.Where(m => m.UserId == _priya && m.RevokedAt == null).ToListAsync();
        Assert.All(places.Where(p => p.OrgId != _armoki), p => Assert.Equal(lastDay, DateOnly.FromDateTime(p.ExpiresAt!.Value.ToOffset(TimeSpan.FromHours(5.5)).DateTime)));
        Assert.Null(places.Single(p => p.OrgId == _armoki).ExpiresAt);
        Assert.True(await db.AuditEvents.AnyAsync(e => e.Action == AuditActions.StaffLeaver && e.TargetId == _priya && e.ActorUserId == _customerAdmin));

        // Now: the places end at once, and Armoki's stays.
        Assert.Equal(2, await Console().LeaverAsync(_customerAdmin, _priya, null, "Left"));
        Assert.Equal([_armoki], await db.OrgMemberships.Where(m => m.UserId == _priya && m.RevokedAt == null).Select(m => m.OrgId).ToListAsync());
    }

    [PostgresFact]
    public async Task AdminRights_GoToPeopleWhoWorkThere_AndEndWithTheirLastPlace()
    {
        await VerifyAsync(_customerAdmin);
        Assert.False((await Console().GrantAsync(_customerAdmin, _lipi, _pune, _appAdmin, reviewer: false)).Succeeded);
        PartnerResult made = await Console().GrantAsync(_customerAdmin, _lipi, _pune, _priya, reviewer: true);
        Assert.True(made.Succeeded, made.Message);
        Assert.True(Assert.Single((await Console().PeopleAsync(_customerAdmin, _lipi, _pune))!, p => p.UserId == _priya).Reviewer);

        // An unverified admin cannot make admins (the DigiLocker rule applies all the way up).
        Assert.False((await Console().GrantAsync(_branchAdmin, _lipi, _pune, _priya, reviewer: false)).Succeeded);

        // When her last place in the tree ends, so does the right.
        Assert.True((await Console().EndAsync(_customerAdmin, _lipi, _pune, _priya, null, "Left")).Succeeded);
        await using SangamDbContext db = _pg.CreateContext();
        Assert.NotNull((await db.OrgAdmins.SingleAsync(a => a.UserId == _priya)).RevokedAt);
        Assert.False((await Console().EndAsync(_customerAdmin, _lipi, _pune, _priya, null, "Left")).Succeeded);
    }

    [PostgresFact]
    public async Task ACustomer_ChoosesWhereRolesAreKept()
    {
        Assert.NotEmpty(await Console().RolesAsync(_customerAdmin, _lipi, _pune));
        Assert.False((await Console().SetRolesKeptAsync(_branchAdmin, _lipi, _pune, false)).Succeeded);
        Assert.True((await Console().SetRolesKeptAsync(_customerAdmin, _lipi, _apulki, false)).Succeeded);
        Assert.Empty(await Console().RolesAsync(_customerAdmin, _lipi, _pune));
        Assert.False((await Console().OverviewAsync(_customerAdmin, _lipi, _apulki))!.RolesKeptInSangam);
    }

    [PostgresFact]
    public async Task ClosingACustomer_EndsEverythingUnderIt_AndTellsThePeople()
    {
        Assert.False((await Console().CloseCustomerAsync(_customerAdmin, _lipi, _apulki, "Contract ended")).Succeeded);
        Assert.False((await Console().CloseCustomerAsync(_appAdmin, _lipi, _apulki, string.Empty)).Succeeded);
        PartnerResult closed = await Console().CloseCustomerAsync(_appAdmin, _lipi, _apulki, "Contract ended");
        Assert.True(closed.Succeeded, closed.Message);

        await using SangamDbContext db = _pg.CreateContext();
        Assert.False(await db.OrgMemberships.AnyAsync(m => m.AppId == _lipi && (m.OrgId == _apulki || m.OrgId == _pune) && m.RevokedAt == null));
        Assert.False(await db.OrgAdmins.AnyAsync(a => a.AppId == _lipi && a.RevokedAt == null));
        Assert.Equal(OrganisationStatus.Closed, (await db.Organisations.SingleAsync(o => o.Id == _pune)).Status);

        // Other applications' Apulki and other customers are untouched; accounts stay.
        Assert.True(await db.OrgMemberships.AnyAsync(m => m.OrgId == _apulkiMedimaze && m.RevokedAt == null));
        Assert.True(await db.OrgMemberships.AnyAsync(m => m.OrgId == _armoki && m.RevokedAt == null));
        Assert.Equal(UserStatus.Active, (await db.Users.SingleAsync(u => u.Id == _priya)).Status);
        InMemoryEmailOutbox outbox = _provider.GetRequiredService<InMemoryEmailOutbox>();
        Assert.Contains(outbox.Recent, m => m.Message.ToEmail == "priya@example.test" && m.Message.Subject.Contains("Apulki no longer uses LiPi", StringComparison.Ordinal));

        // A closed customer leaves the console.
        Assert.DoesNotContain(await Console().ScopesAsync(_customerAdmin), s => s.AppId == _lipi);
    }

    private static Organisation Root(Guid appId, string name, DateTimeOffset now)
    {
        Guid id = Guid.NewGuid();
        return new Organisation { Id = id, Name = name, OrgTypeCode = "hospital", Path = OrganisationPath.ForRoot(id), RegisteredViaAppId = appId, CreatedAt = now, UpdatedAt = now };
    }

    private static Organisation Child(Organisation parent, string name, DateTimeOffset now)
    {
        Guid id = Guid.NewGuid();
        return new Organisation { Id = id, Name = name, OrgTypeCode = "hospital", ParentOrgId = parent.Id, Path = OrganisationPath.ForChild(parent.Path, id), Depth = 1, RegisteredViaAppId = parent.RegisteredViaAppId, CreatedAt = now, UpdatedAt = now };
    }

    private static SangamUser Person(string email, string first, string last, DateTimeOffset now)
    {
        SangamUser user = TestUsers.New(email, null);
        user.FirstName = first;
        user.LastName = last;
        user.CreatedAt = now;
        return user;
    }

    private static OrgMembership Place(Guid appId, Guid orgId, Guid userId, Guid? roleId, DateTimeOffset now, DateTimeOffset? until = null)
        => new() { Id = Guid.NewGuid(), AppId = appId, OrgId = orgId, UserId = userId, RoleId = roleId, GrantedAt = now, ExpiresAt = until };

    private async Task VerifyAsync(Guid userId)
    {
        await using SangamDbContext db = _pg.CreateContext();
        (await db.Users.SingleAsync(u => u.Id == userId)).IdentityVerifiedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
    }

    private IOrgConsoleService Console()
    {
        IServiceScope scope = _provider.CreateScope();
        _scopes.Add(scope);
        return scope.ServiceProvider.GetRequiredService<IOrgConsoleService>();
    }
}
