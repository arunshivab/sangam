using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Sangam.Identity.Application.Devices;
using Sangam.Identity.Domain.Entities;
using Sangam.Identity.Domain.Enums;
using Sangam.Identity.Infrastructure.Persistence;
using Sangam.Identity.Infrastructure.Tenancy;
using Sangam.Identity.Infrastructure.Tests.Accounts;
using Sangam.Identity.Infrastructure.Tests.Postgres;

namespace Sangam.Identity.Infrastructure.Tests.Tenancy;

/// <summary>
/// rc.12 (T1): shared ward devices. Only admins of the organisation enrol and remove; tiles show only that hospital's staff;
/// a lock tells the person's other applications; a customer may make the rules stricter, never looser; removing a device
/// ends its sessions.
/// </summary>
[Collection("postgres")]
public sealed class SharedDeviceServiceTests : IAsyncLifetime
{
    private readonly List<IServiceScope> _scopes = [];
    private readonly PostgresFixture _pg;
    private ServiceProvider _provider = null!;
    private Guid _lipi;
    private Guid _labs;
    private Guid _apulki;
    private Guid _ward;
    private Guid _admin;
    private Guid _nurse;
    private Guid _outsider;

    public SharedDeviceServiceTests(PostgresFixture pg)
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
        App lipi = new() { Id = Guid.NewGuid(), ClientId = "lipi-ward", Slug = "lipi-ward", DisplayName = "LiPi", OwnerCompanyName = "imagiQa", CreatedAt = now, UpdatedAt = now };
        App labs = new() { Id = Guid.NewGuid(), ClientId = "labs-ward", Slug = "labs-ward", DisplayName = "Labs", OwnerCompanyName = "imagiQa", CreatedAt = now, UpdatedAt = now };
        db.Apps.AddRange(lipi, labs);
        Guid rootId = Guid.NewGuid();
        Guid wardId = Guid.NewGuid();
        Organisation apulki = new() { Id = rootId, Name = "Apulki", OrgTypeCode = "hospital", Path = OrganisationPath.ForRoot(rootId), RegisteredViaAppId = lipi.Id, CreatedAt = now, UpdatedAt = now };
        Organisation ward = new() { Id = wardId, Name = "Ward 4B", OrgTypeCode = "hospital", ParentOrgId = rootId, Path = OrganisationPath.ForChild(apulki.Path, wardId), Depth = 1, RegisteredViaAppId = lipi.Id, CreatedAt = now, UpdatedAt = now };
        db.Organisations.AddRange(apulki, ward);
        Role nurseRole = new() { Id = Guid.NewGuid(), AppId = lipi.Id, Code = "nurse", DisplayName = "Nurse", CreatedAt = now };
        db.Roles.Add(nurseRole);
        SangamUser admin = TestUsers.New("bala@apulki.test", null);
        admin.FirstName = "Bala";
        SangamUser nurse = TestUsers.New("meera@apulki.test", null);
        nurse.FirstName = "Meera";
        nurse.LastName = "Iyer";
        SangamUser outsider = TestUsers.New("visitor@example.test", null);
        outsider.FirstName = "Visitor";
        db.Users.AddRange(admin, nurse, outsider);
        db.OrgMemberships.AddRange(
            new OrgMembership { Id = Guid.NewGuid(), AppId = lipi.Id, OrgId = wardId, UserId = admin.Id, GrantedAt = now },
            new OrgMembership { Id = Guid.NewGuid(), AppId = lipi.Id, OrgId = wardId, UserId = nurse.Id, RoleId = nurseRole.Id, GrantedAt = now });
        db.OrgAdmins.Add(new OrgAdmin { Id = Guid.NewGuid(), AppId = lipi.Id, OrgId = rootId, UserId = admin.Id, Right = OrgAdminRight.Admin, GrantedAt = now });
        db.WebhookEndpoints.Add(new WebhookEndpoint { Id = Guid.NewGuid(), AppId = labs.Id, Url = "https://labs.example.in/hooks", Events = AppEventTypes.SessionLocked, ProtectedSecret = "x", CreatedAt = now, UpdatedAt = now });
        await db.SaveChangesAsync();
        (_lipi, _labs, _apulki, _ward, _admin, _nurse, _outsider) = (lipi.Id, labs.Id, rootId, wardId, admin.Id, nurse.Id, outsider.Id);
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
    [InlineData("482916", true)]
    [InlineData("111111", false)]
    [InlineData("123456", false)]
    [InlineData("987654", false)]
    [InlineData("12345", false)]
    [InlineData("12a456", false)]
    public void APin_IsSixDigits_NotARunOrRepeat(string pin, bool ok) => Assert.Equal(ok, EfSharedDeviceService.IsAcceptablePin(pin));

    [PostgresFact]
    public async Task OnlyAdminsEnrol_AndTilesShowOnlyTheHospitalsStaff()
    {
        Assert.Equal(DeviceEnrolStatus.NotAdmin, (await Devices().StartEnrolAsync(_lipi, _nurse, _ward, "Ward 4B")).Status);
        Assert.Equal(DeviceEnrolStatus.LabelRequired, (await Devices().StartEnrolAsync(_lipi, _admin, _ward, "  ")).Status);
        DeviceEnrolStart start = await Devices().StartEnrolAsync(_lipi, _admin, _ward, "Ward 4B · Nursing station");
        Assert.Null(await Devices().CompleteEnrolAsync(start.Token!, _nurse));
        DeviceEnrolled enrolled = (await Devices().CompleteEnrolAsync(start.Token!, _admin))!;
        Assert.Null(await Devices().CompleteEnrolAsync(start.Token!, _admin));
        SharedDeviceInfo device = (await Devices().FindAsync(enrolled.Key))!;
        Assert.Equal("Ward 4B", device.OrgName);
        Assert.Equal(DeviceRules.Sangam, device.Rules);

        Guid nurseSession = await SessionAsync(_nurse);
        await Devices().SignedInAsync(device.Id, new DeviceSignIn(_nurse, nurseSession, SignInMode.Password, ["pwd"], false));
        await Devices().SignedInAsync(device.Id, new DeviceSignIn(_outsider, await SessionAsync(_outsider), SignInMode.Password, ["pwd"], false));
        DeviceTile tile = Assert.Single(await Devices().TilesAsync(device.Id));
        Assert.Equal("Meera Iyer", tile.Name);
        Assert.Equal("Nurse", tile.Role);
        Assert.False(tile.Locked);
        Assert.Equal(DeviceGate.OfferPin, await Devices().GateAsync(nurseSession, device.Id));
        Assert.Equal(DeviceGate.None, await Devices().GateAsync(nurseSession, null));

        Assert.Single(await Devices().ListAsync(_lipi, _admin, _apulki));
        Assert.Empty(await Devices().ListAsync(_lipi, _nurse, _ward));
    }

    [PostgresFact]
    public async Task ALock_TellsTheOtherApplications_AndAPinGivesTheSessionBack()
    {
        (SharedDeviceInfo device, Guid session) = await EnrolledWithNurseAsync();
        await using (SangamDbContext db = _pg.CreateContext())
        {
            db.SessionApps.AddRange(
                new SessionApp { SessionId = session, AppId = _lipi, UserId = _nurse, FirstSeenAt = DateTimeOffset.UtcNow },
                new SessionApp { SessionId = session, AppId = _labs, UserId = _nurse, FirstSeenAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }

        Assert.True(await Devices().SetPinAsync(_nurse, "482916"));
        Assert.True(await Devices().LockAsync(session, _nurse, _lipi));
        Assert.False(await Devices().LockAsync(session, _outsider, _lipi));
        await using (SangamDbContext db = _pg.CreateContext())
        {
            AppEvent told = Assert.Single(await db.AppEvents.Where(e => e.Type == AppEventTypes.SessionLocked).ToListAsync());
            Assert.Equal(_labs, told.AppId);
        }

        Assert.Equal(DeviceGate.Unlock, await Devices().GateAsync(session, device.Id));
        Assert.Equal(DeviceGate.SignInAgain, await Devices().GateAsync(session, null));
        Guid tile = (await Devices().LockedTileAsync(device.Id, session))!.Value;
        Assert.True(Assert.Single(await Devices().TilesAsync(device.Id)).Locked);
        PinUnlockResult wrong = await Devices().UnlockAsync(device.Id, tile, "000111");
        Assert.Equal((PinUnlockStatus.Wrong, 4), (wrong.Status, wrong.TriesLeft));
        PinUnlockResult right = await Devices().UnlockAsync(device.Id, tile, "482916");
        Assert.Equal(PinUnlockStatus.Unlocked, right.Status);
        Assert.Equal(session, right.Resume!.SessionId);
        Assert.Equal(DeviceGate.None, await Devices().GateAsync(session, device.Id));
    }

    [PostgresFact]
    public async Task TheCustomerMayOnlyBeStricter_AndAnOldLockNeedsAFullSignIn()
    {
        (SharedDeviceInfo device, Guid session) = await EnrolledWithNurseAsync();
        Assert.False(await Devices().SetRulesAsync(_lipi, _nurse, _apulki, new DeviceRules(30, 8)));
        Assert.False(await Devices().SetRulesAsync(_lipi, _admin, _ward, new DeviceRules(30, 8)));
        Assert.True(await Devices().SetRulesAsync(_lipi, _admin, _apulki, new DeviceRules(500, 48)));
        Assert.Equal(DeviceRules.Sangam, await Devices().RulesAsync(_apulki));
        Assert.True(await Devices().SetRulesAsync(_lipi, _admin, _apulki, new DeviceRules(30, 8)));
        Assert.Equal(new DeviceRules(30, 8), await Devices().RulesAsync(_apulki));

        Assert.True(await Devices().SetPinAsync(_nurse, "482916"));
        Assert.True(await Devices().LockAsync(session, _nurse, _lipi));
        await using (SangamDbContext db = _pg.CreateContext())
        {
            DateTimeOffset longAgo = DateTimeOffset.UtcNow.AddMinutes(-31);
            await db.SharedDevicePeople.Where(p => p.SessionId == session).ExecuteUpdateAsync(u => u.SetProperty(p => p.LockedAt, longAgo));
        }

        Assert.Equal(DeviceGate.SignInAgain, await Devices().GateAsync(session, device.Id));
        Guid tile = await TileIdAsync(session);
        Assert.Equal(PinUnlockStatus.SignInAgain, (await Devices().UnlockAsync(device.Id, tile, "482916")).Status);
    }

    [PostgresFact]
    public async Task FiveWrongPins_CancelThePin_UntilTheNextFullSignIn()
    {
        (SharedDeviceInfo device, Guid session) = await EnrolledWithNurseAsync();
        Assert.True(await Devices().SetPinAsync(_nurse, "482916"));
        Assert.True(await Devices().LockAsync(session, _nurse, _lipi));
        Guid tile = await TileIdAsync(session);
        for (int i = 0; i < 4; i++)
        {
            Assert.Equal(PinUnlockStatus.Wrong, (await Devices().UnlockAsync(device.Id, tile, "000111")).Status);
        }

        Assert.Equal(PinUnlockStatus.Cancelled, (await Devices().UnlockAsync(device.Id, tile, "000111")).Status);
        Assert.Equal(PinUnlockStatus.Cancelled, (await Devices().UnlockAsync(device.Id, tile, "482916")).Status);
        Assert.False(await Devices().HasPinAsync(_nurse));
        Assert.Equal(DeviceGate.SignInAgain, await Devices().GateAsync(session, device.Id));

        await Devices().SignedInAsync(device.Id, new DeviceSignIn(_nurse, await SessionAsync(_nurse), SignInMode.Password, ["pwd"], false));
        Assert.True(await Devices().HasPinAsync(_nurse));
    }

    [PostgresFact]
    public async Task RemovingADevice_EndsItsSessions()
    {
        (SharedDeviceInfo device, Guid session) = await EnrolledWithNurseAsync();
        Assert.False(await Devices().RemoveAsync(_lipi, _nurse, device.Id));
        Assert.True(await Devices().RemoveAsync(_lipi, _admin, device.Id));
        await using SangamDbContext db = _pg.CreateContext();
        Assert.Equal("device_removed", (await db.UserSessions.SingleAsync(s => s.Id == session)).RevokedReason);
        Assert.Empty(await Devices().ListAsync(_lipi, _admin, _apulki));
    }

    private async Task<(SharedDeviceInfo Device, Guid Session)> EnrolledWithNurseAsync()
    {
        DeviceEnrolStart start = await Devices().StartEnrolAsync(_lipi, _admin, _ward, "Ward 4B · Nursing station");
        DeviceEnrolled enrolled = (await Devices().CompleteEnrolAsync(start.Token!, _admin))!;
        SharedDeviceInfo device = (await Devices().FindAsync(enrolled.Key))!;
        Guid session = await SessionAsync(_nurse);
        await Devices().SignedInAsync(device.Id, new DeviceSignIn(_nurse, session, SignInMode.Password, ["pwd"], false));
        return (device, session);
    }

    private async Task<Guid> SessionAsync(Guid userId)
    {
        await using SangamDbContext db = _pg.CreateContext();
        Guid id = Guid.NewGuid();
        db.UserSessions.Add(new UserSession { Id = id, UserId = userId, SignInMode = SignInMode.Password, CreatedAt = DateTimeOffset.UtcNow, LastSeenAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        return id;
    }

    private async Task<Guid> TileIdAsync(Guid session)
    {
        await using SangamDbContext db = _pg.CreateContext();
        return await db.SharedDevicePeople.Where(p => p.SessionId == session).Select(p => p.Id).SingleAsync();
    }

    private ISharedDeviceService Devices()
    {
        IServiceScope scope = _provider.CreateScope();
        _scopes.Add(scope);
        return scope.ServiceProvider.GetRequiredService<ISharedDeviceService>();
    }
}
