using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Sangam.Identity.Application.Abstractions;
using Sangam.Identity.Application.Tenancy;
using Sangam.Identity.Domain;
using Sangam.Identity.Domain.Entities;
using Sangam.Identity.Infrastructure.Persistence;
using Sangam.Identity.Infrastructure.Services;
using Sangam.Identity.Infrastructure.Tenancy;
using Sangam.Identity.Infrastructure.Tests.Accounts;
using Sangam.Identity.Infrastructure.Tests.Postgres;

namespace Sangam.Identity.Infrastructure.Tests.Tenancy;

/// <summary>
/// rc.12 (founder decisions, 14 Oct 2026): adding staff safely. Only a DigiLocker-verified admin adds; the check says which
/// details match, never what Sangam holds; a person joins only by their own act (the admin's code, or their own join code).
/// </summary>
[Collection("postgres")]
public sealed partial class StaffServiceTests : IAsyncLifetime
{
    private readonly List<IServiceScope> _scopes = [];
    private readonly PostgresFixture _pg;
    private readonly MutableClock _clock = new();
    private ServiceProvider _provider = null!;
    private Guid _app;
    private Guid _group;
    private Guid _pune;
    private Guid _mumbai;
    private Guid _admin;
    private Guid _unverified;
    private Guid _priya;

    public StaffServiceTests(PostgresFixture pg)
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
            ["Sangam:Issuer"] = "https://id.sangam.test",
        }).Build();
        services.AddSingleton(configuration);
        services.AddSangamInfrastructure(configuration);
        services.AddSingleton<IClock>(_clock);
        _provider = services.BuildServiceProvider();

        DateTimeOffset now = DateTimeOffset.UtcNow;
        await using SangamDbContext db = _pg.CreateContext();
        App lipi = new() { Id = Guid.NewGuid(), ClientId = "lipi-staff", Slug = "lipi-staff", DisplayName = "LiPi HIS", OwnerCompanyName = "imagiQa", CreatedAt = now, UpdatedAt = now };
        db.Apps.Add(lipi);
        Guid groupId = Guid.NewGuid();
        Organisation group = new() { Id = groupId, Name = "Apulki", OrgTypeCode = "hospital", Path = OrganisationPath.ForRoot(groupId), RegisteredViaAppId = lipi.Id, CreatedAt = now, UpdatedAt = now };
        Guid puneId = Guid.NewGuid();
        Guid mumbaiId = Guid.NewGuid();
        db.Organisations.AddRange(
            group,
            new Organisation { Id = puneId, Name = "Apulki Pune", OrgTypeCode = "hospital", ParentOrgId = groupId, Path = OrganisationPath.ForChild(group.Path, puneId), Depth = 1, RegisteredViaAppId = lipi.Id, CreatedAt = now, UpdatedAt = now },
            new Organisation { Id = mumbaiId, Name = "Apulki Mumbai", OrgTypeCode = "hospital", ParentOrgId = groupId, Path = OrganisationPath.ForChild(group.Path, mumbaiId), Depth = 1, RegisteredViaAppId = lipi.Id, CreatedAt = now, UpdatedAt = now });
        db.Roles.AddRange(
            new Role { Id = Guid.NewGuid(), AppId = lipi.Id, Code = "doctor", DisplayName = "Doctor", CreatedAt = now },
            new Role { Id = Guid.NewGuid(), AppId = lipi.Id, Code = "administrator", DisplayName = "Administrator", CreatedAt = now });

        SangamUser admin = TestUsers.New("arun@apulki.test", "+919800000001");
        admin.FirstName = "Arun";
        admin.LastName = "Shiva";
        admin.IdentityVerifiedAt = now;
        SangamUser unverified = TestUsers.New("bala@apulki.test", "+919800000002");
        unverified.FirstName = "Bala";
        SangamUser priya = TestUsers.New("priya@example.test", "+919800000003");
        priya.FirstName = "Priya";
        priya.LastName = "Sharma";
        priya.DateOfBirth = new DateOnly(1988, 4, 12);
        db.Users.AddRange(admin, unverified, priya);
        db.OrgAdmins.AddRange(
            new OrgAdmin { Id = Guid.NewGuid(), AppId = lipi.Id, OrgId = groupId, UserId = admin.Id, Right = OrgAdminRight.Admin, GrantedAt = now },
            new OrgAdmin { Id = Guid.NewGuid(), AppId = lipi.Id, OrgId = puneId, UserId = unverified.Id, Right = OrgAdminRight.Admin, GrantedAt = now });
        await db.SaveChangesAsync();
        (_app, _group, _pune, _mumbai, _admin, _unverified, _priya) = (lipi.Id, groupId, puneId, mumbaiId, admin.Id, unverified.Id, priya.Id);
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
    public async Task OnlyAVerifiedAdmin_Adds_UnlessTheTemporaryExceptionIsOn()
    {
        StaffAddition someone = new(Details: Ravi());
        StaffAddResult refused = await Staff().AddAsync(new StaffActor(_app, _unverified), _pune, someone);
        Assert.Equal(StaffAddStatus.Refused, refused.Status);
        Assert.Contains("DigiLocker", refused.Message, StringComparison.Ordinal);

        // Nor anywhere they are not an admin, verified or not.
        Assert.Equal(StaffAddStatus.NotFound, (await Staff().AddAsync(new StaffActor(_app, _unverified), _mumbai, someone)).Status);
        Assert.Null(await Staff().CheckAsync(new StaffActor(_app, _unverified), _mumbai, Ravi()));

        await using (SangamDbContext db = _pg.CreateContext())
        {
            (await db.Apps.SingleAsync(a => a.Id == _app)).AddWithoutDigiLockerUntil = DateTimeOffset.UtcNow.AddDays(60);
            await db.SaveChangesAsync();
        }

        StaffAddResult added = await Staff().AddAsync(new StaffActor(_app, _unverified), _pune, someone);
        Assert.Equal(StaffAddStatus.Waiting, added.Status);
        Assert.True(added.AddedBeforeDigiLocker);
        await using SangamDbContext check = _pg.CreateContext();
        Assert.True((await check.StaffInvites.SingleAsync()).AddedBeforeDigiLocker);
    }

    [PostgresFact]
    public async Task TheCheck_SaysWhichDetailsMatch_NeverWhatSangamHolds_AndIsLimited()
    {
        StaffActor actor = new(_app, _admin);
        StaffCheckResult same = (await Staff().CheckAsync(actor, _pune, new StaffDetails("Priya", "Sharma", "priya@example.test", null, new DateOnly(1988, 4, 12))))!;
        Assert.Equal(StaffCheckOutcome.Match, same.Outcome);

        StaffCheckResult differs = (await Staff().CheckAsync(actor, _pune, new StaffDetails("Pria", "Sharma", "PRIYA@example.test", "+919800000003", new DateOnly(1988, 4, 12))))!;
        Assert.Equal(StaffCheckOutcome.Differs, differs.Outcome);
        Assert.Equal(["first_name"], differs.Differing);
        Assert.Equal("A person with this last name, date of birth, email and mobile number is already registered in Sangam, but the first name is different. Are you sure you have checked the details against their records?", differs.Message);
        Assert.DoesNotContain("Priya", differs.Message, StringComparison.Ordinal);

        // Found by the mobile alone: the email the admin typed is not theirs.
        StaffCheckResult byMobile = (await Staff().CheckAsync(actor, _pune, new StaffDetails("Priya", "Sharma", "p.sharma@other.test", "+919800000003", new DateOnly(1988, 4, 12))))!;
        Assert.Equal(["email"], byMobile.Differing);

        StaffCheckResult similar = (await Staff().CheckAsync(actor, _pune, new StaffDetails("priya ", "SHARMA", "new@example.test", null, new DateOnly(1988, 4, 12))))!;
        Assert.Equal(StaffCheckOutcome.Similar, similar.Outcome);
        Assert.Equal(StaffCheckOutcome.None, (await Staff().CheckAsync(actor, _pune, Ravi()))!.Outcome);

        // Five checks a day about one person (by the email typed).
        for (int i = 0; i < 2; i++)
        {
            await Staff().CheckAsync(actor, _pune, new StaffDetails("Priya", "Sharma", "priya@example.test", null, new DateOnly(1988, 4, 12)));
        }

        StaffCheckResult last = (await Staff().CheckAsync(actor, _pune, new StaffDetails("Priya", "Sharma", "priya@example.test", null, new DateOnly(1988, 4, 12))))!;
        Assert.Equal(0, last.ChecksLeftToday);
        StaffAddResult tooMany = await Staff().AddAsync(actor, _pune, new StaffAddition(Details: new StaffDetails("Priya", "Sharma", "priya@example.test", null, new DateOnly(1988, 4, 12))));
        Assert.Equal(StaffAddStatus.TooMany, tooMany.Status);

        await using SangamDbContext db = _pg.CreateContext();
        List<string> metadata = await db.AuditEvents.Where(e => e.Action == AuditActions.StaffCheck).Select(e => e.Metadata).ToListAsync();
        Assert.NotEmpty(metadata);
        Assert.DoesNotContain(metadata, m => m.Contains("Priya", StringComparison.OrdinalIgnoreCase) || m.Contains("1988", StringComparison.Ordinal));
    }

    [PostgresFact]
    public async Task ANewPerson_JoinsOnlyWithTheAdminsCode_InEveryOrganisationTheyWereAddedTo()
    {
        StaffActor actor = new(_app, _admin);
        StaffAddResult first = await Staff().AddAsync(actor, _pune, new StaffAddition(Details: Ravi(), Roles: ["doctor", "administrator"]));
        Assert.Equal(StaffAddStatus.Waiting, first.Status);
        Assert.Matches("^[A-Z2-9]{4}-[A-Z2-9]{4}$", first.ActivationCode!);
        Assert.Null(first.UserId);

        // The second organisation goes on the same activation: no second code or e-mail.
        StaffAddResult second = await Staff().AddAsync(actor, _mumbai, new StaffAddition(StaffRef: first.StaffRef, Roles: ["doctor"]));
        Assert.Equal(StaffAddStatus.Waiting, second.Status);
        Assert.Null(second.ActivationCode);

        InMemoryEmailOutbox outbox = _provider.GetRequiredService<InMemoryEmailOutbox>();
        EmailMessage mail = Assert.Single(outbox.Recent, m => m.Message.ToEmail == "ravi@example.test").Message;
        Assert.DoesNotContain(first.ActivationCode!, mail.TextBody, StringComparison.Ordinal);
        string token = LinkToken().Match(mail.TextBody).Groups[1].Value;

        StaffInviteView view = (await Staff().GetInviteAsync(token))!;
        Assert.False(view.ForExistingAccount);
        Assert.Equal("Arun Shiva", view.AddedBy);
        Assert.Equal(["Apulki Mumbai", "Apulki Pune"], Assert.Single(view.Places).Organisations);

        // The wrong code four times, then the right one.
        for (int i = 4; i >= 1; i--)
        {
            StaffCodeResult wrong = await Staff().CheckCodeAsync(token, "AAAA-AAAA");
            Assert.Equal((StaffCodeStatus.Wrong, i), (wrong.Status, wrong.AttemptsLeft));
        }

        StaffCodeResult right = await Staff().CheckCodeAsync(token, first.ActivationCode!.ToLowerInvariant().Replace("-", " ", StringComparison.Ordinal));
        Assert.Equal(StaffCodeStatus.Right, right.Status);

        // A different person cannot use it; the person with that address can.
        Assert.Equal(StaffJoinStatus.Refused, (await Staff().JoinAsync(right.InviteId!.Value, _priya)).Status);
        Guid ravi = await NewUserAsync("ravi@example.test", "Ravi", "Kumar");
        Assert.Equal(StaffJoinStatus.Joined, (await Staff().JoinAsync(right.InviteId.Value, ravi)).Status);
        Assert.Equal(StaffJoinStatus.Refused, (await Staff().JoinAsync(right.InviteId.Value, ravi)).Status);

        await using SangamDbContext db = _pg.CreateContext();
        List<string> pune = await db.OrgMemberships.Where(m => m.UserId == ravi && m.OrgId == _pune && m.RevokedAt == null).Select(m => m.Role!.Code).ToListAsync();
        Assert.Equal(["administrator", "doctor"], pune.Order(StringComparer.Ordinal));
        Assert.True(await db.OrgMemberships.AnyAsync(m => m.UserId == ravi && m.OrgId == _mumbai && m.RevokedAt == null));
        Assert.True(await db.AppGrants.AnyAsync(g => g.UserId == ravi && g.AppId == _app && g.RevokedAt == null));
        AuditEvent grant = await db.AuditEvents.FirstAsync(e => e.Action == AuditActions.OrgMembershipGrant && e.TargetId == ravi);
        Assert.Equal(_admin, grant.ActorUserId);
    }

    [PostgresFact]
    public async Task FiveWrongCodes_EndTheLink()
    {
        StaffAddResult added = await Staff().AddAsync(new StaffActor(_app, _admin), _pune, new StaffAddition(Details: Ravi()));
        string token = LinkToken().Match(_provider.GetRequiredService<InMemoryEmailOutbox>().LatestFor("ravi@example.test")!.Message.TextBody).Groups[1].Value;
        for (int i = 0; i < 4; i++)
        {
            await Staff().CheckCodeAsync(token, "BBBB-BBBB");
        }

        Assert.Equal(StaffCodeStatus.Locked, (await Staff().CheckCodeAsync(token, "BBBB-BBBB")).Status);
        Assert.Equal(StaffCodeStatus.NotOpen, (await Staff().CheckCodeAsync(token, added.ActivationCode!)).Status);
        Assert.Equal(StaffInviteState.Cancelled, (await Staff().GetInviteAsync(token))!.State);
    }

    [PostgresFact]
    public async Task AnExistingAccount_MustMatch_UnlessTheAdminConfirmedTheDifference()
    {
        StaffActor actor = new(_app, _admin);
        StaffDetails misspelt = new("Pria", "Sharma", "priya@example.test", null, new DateOnly(1988, 4, 12));
        StaffAddResult asked = await Staff().AddAsync(actor, _pune, new StaffAddition(Details: misspelt, Roles: ["doctor"]));
        Assert.Equal(StaffAddStatus.NeedsConfirmation, asked.Status);
        Assert.Equal(["first_name"], asked.Check!.Differing);

        StaffAddResult confirmed = await Staff().AddAsync(actor, _pune, new StaffAddition(Details: misspelt, Roles: ["doctor"], Confirmed: true));
        Assert.Equal(StaffAddStatus.Waiting, confirmed.Status);
        Guid invite = confirmed.StaffRef!.Value;
        string token = LinkToken().Match(_provider.GetRequiredService<InMemoryEmailOutbox>().LatestFor("priya@example.test")!.Message.TextBody).Groups[1].Value;
        Assert.True((await Staff().GetInviteAsync(token))!.ForExistingAccount);
        Assert.Equal(StaffCodeStatus.Right, (await Staff().CheckCodeAsync(token, confirmed.ActivationCode!)).Status);

        // Meanwhile Priya's date of birth on her account is different from what the admin entered: joining stops.
        await using (SangamDbContext db = _pg.CreateContext())
        {
            (await db.Users.SingleAsync(u => u.Id == _priya)).DateOfBirth = new DateOnly(1988, 4, 21);
            await db.SaveChangesAsync();
        }

        Assert.Equal(StaffJoinStatus.Stopped, (await Staff().JoinAsync(invite, _priya)).Status);
        InMemoryEmailOutbox outbox = _provider.GetRequiredService<InMemoryEmailOutbox>();
        EmailMessage told = outbox.LatestFor("arun@apulki.test")!.Message;
        Assert.Contains("date of birth", told.TextBody, StringComparison.Ordinal);
        Assert.DoesNotContain("1988", told.TextBody, StringComparison.Ordinal);

        await using (SangamDbContext db = _pg.CreateContext())
        {
            Assert.False(await db.OrgMemberships.AnyAsync(m => m.UserId == _priya));
            (await db.Users.SingleAsync(u => u.Id == _priya)).DateOfBirth = new DateOnly(1988, 4, 12);
            await db.SaveChangesAsync();
        }

        // The confirmed first name does not stop her; the account the check found is the only one that can join.
        Assert.Equal(StaffJoinStatus.Refused, (await Staff().JoinAsync(invite, _admin)).Status);
        Assert.Equal(StaffJoinStatus.Joined, (await Staff().JoinAsync(invite, _priya)).Status);
    }

    [PostgresFact]
    public async Task AJoinCode_AddsThatAccount_OnceAndOnlyForTheAdminWhoUsedIt()
    {
        JoinCodeIssued code = await Scoped<IJoinCodeService>().CreateAsync(_priya);
        Assert.Contains("<svg", code.QrSvg, StringComparison.Ordinal);

        StaffAddResult pune = await Staff().AddAsync(new StaffActor(_app, _admin), _pune, new StaffAddition(JoinCode: code.Code.ToLowerInvariant(), Roles: ["doctor"]));
        Assert.Equal((StaffAddStatus.Added, _priya), (pune.Status, pune.UserId!.Value));

        // The same admin, ticking a second organisation, may use it again for a few minutes; nobody else may.
        Assert.Equal(StaffAddStatus.Added, (await Staff().AddAsync(new StaffActor(_app, _admin), _mumbai, new StaffAddition(JoinCode: code.Code))).Status);
        await using (SangamDbContext db = _pg.CreateContext())
        {
            (await db.Users.SingleAsync(u => u.Id == _unverified)).IdentityVerifiedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }

        Assert.Equal(StaffAddStatus.NotFound, (await Staff().AddAsync(new StaffActor(_app, _unverified), _pune, new StaffAddition(JoinCode: code.Code))).Status);
        _clock.Advance(EfStaffService.JoinCodeBatch + TimeSpan.FromMinutes(1));
        Assert.Equal(StaffAddStatus.NotFound, (await Staff().AddAsync(new StaffActor(_app, _admin), _group, new StaffAddition(JoinCode: code.Code))).Status);

        // Someone already in the customer can be added to another branch by id; a stranger cannot.
        Assert.Equal(StaffAddStatus.Added, (await Staff().AddAsync(new StaffActor(_app, _admin), _group, new StaffAddition(UserId: _priya))).Status);
        Assert.Equal(StaffAddStatus.NotFound, (await Staff().AddAsync(new StaffActor(_app, _admin), _group, new StaffAddition(UserId: _unverified))).Status);

        await using SangamDbContext check = _pg.CreateContext();
        Assert.Equal(3, await check.OrgMemberships.Where(m => m.UserId == _priya && m.RevokedAt == null).Select(m => m.OrgId).Distinct().CountAsync());
        // Staff without a role in Sangam at the group: one row with no role.
        Assert.Null((await check.OrgMemberships.SingleAsync(m => m.UserId == _priya && m.OrgId == _group)).RoleId);
    }

    [PostgresFact]
    public async Task AJoiner_StartsOnTheirDate()
    {
        JoinCodeIssued code = await Scoped<IJoinCodeService>().CreateAsync(_priya);
        DateOnly today = StaffCodes.IndiaToday(_clock.UtcNow);
        StaffAddResult later = await Staff().AddAsync(new StaffActor(_app, _admin), _pune, new StaffAddition(JoinCode: code.Code, Roles: ["doctor"], StartsOn: today.AddDays(3)));
        Assert.Equal(StaffAddStatus.Waiting, later.Status);
        Assert.Equal(StaffAddStatus.Refused, (await Staff().AddAsync(new StaffActor(_app, _admin), _pune, new StaffAddition(UserId: _priya, StartsOn: today.AddDays(-1)))).Status);

        IReadOnlyList<StaffWaiting> waiting = (await Staff().WaitingAsync(new StaffActor(_app, _admin), _pune))!;
        StaffWaiting row = Assert.Single(waiting);
        Assert.True(row.Activated);
        Assert.Equal("Priya Sharma", row.Name);

        Assert.Equal(0, await Scoped<EfStaffService>().ApplyDueAsync());
        _clock.Advance(TimeSpan.FromDays(3));
        Assert.Equal(1, await Scoped<EfStaffService>().ApplyDueAsync());
        await using SangamDbContext db = _pg.CreateContext();
        Assert.True(await db.OrgMemberships.AnyAsync(m => m.UserId == _priya && m.OrgId == _pune && m.RevokedAt == null));
        Assert.Empty((await Staff().WaitingAsync(new StaffActor(_app, _admin), _pune))!);
    }

    [PostgresFact]
    public async Task AWaitingAddition_CanBeCancelled_AndTheLinkWithIt()
    {
        StaffAddResult added = await Staff().AddAsync(new StaffActor(_app, _admin), _pune, new StaffAddition(Details: Ravi()));
        StaffWaiting row = Assert.Single((await Staff().WaitingAsync(new StaffActor(_app, _admin), _pune))!);
        Assert.Equal(added.StaffRef, row.StaffRef);
        Assert.False(row.Activated);
        Assert.True(await Staff().CancelAsync(new StaffActor(_app, _admin), row.Id));
        Assert.False(await Staff().CancelAsync(new StaffActor(_app, _admin), row.Id));
        string token = LinkToken().Match(_provider.GetRequiredService<InMemoryEmailOutbox>().LatestFor("ravi@example.test")!.Message.TextBody).Groups[1].Value;
        Assert.Equal(StaffInviteState.Cancelled, (await Staff().GetInviteAsync(token))!.State);
    }

    [PostgresFact]
    public async Task ACustomerThatKeepsRolesInTheApplication_AddsWithoutRoles()
    {
        await using (SangamDbContext db = _pg.CreateContext())
        {
            (await db.Organisations.SingleAsync(o => o.Id == _group)).RolesKeptInSangam = false;
            await db.SaveChangesAsync();
        }

        JoinCodeIssued code = await Scoped<IJoinCodeService>().CreateAsync(_priya);
        Assert.Equal(StaffAddStatus.Refused, (await Staff().AddAsync(new StaffActor(_app, _admin), _mumbai, new StaffAddition(JoinCode: code.Code, Roles: ["doctor"]))).Status);
        Assert.Equal(StaffAddStatus.Added, (await Staff().AddAsync(new StaffActor(_app, _admin), _mumbai, new StaffAddition(JoinCode: code.Code))).Status);
    }

    private static StaffDetails Ravi() => new("Ravi", "Kumar", "ravi@example.test", "+919811111111", new DateOnly(1992, 7, 1));

    [GeneratedRegex(@"/staff/([A-Za-z0-9_-]+)")]
    private static partial Regex LinkToken();

    private async Task<Guid> NewUserAsync(string email, string first, string last)
    {
        await using SangamDbContext db = _pg.CreateContext();
        SangamUser user = TestUsers.New(email, null);
        user.FirstName = first;
        user.LastName = last;
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    private IStaffService Staff() => Scoped<IStaffService>();

    private T Scoped<T>()
        where T : notnull
    {
        IServiceScope scope = _provider.CreateScope();
        _scopes.Add(scope);
        return scope.ServiceProvider.GetRequiredService<T>();
    }

    private sealed class MutableClock : IClock
    {
        private TimeSpan _offset;

        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow + _offset;

        public void Advance(TimeSpan by) => _offset += by;
    }
}
