using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Sangam.Identity.Application.Accounts;
using Sangam.Identity.Application.Portal;
using Sangam.Identity.Domain;
using Sangam.Identity.Domain.Entities;
using Sangam.Identity.Domain.Enums;
using Sangam.Identity.Infrastructure.Maintenance;
using Sangam.Identity.Infrastructure.Persistence;
using Sangam.Identity.Infrastructure.Services;
using Sangam.Identity.Infrastructure.Tests.Postgres;

namespace Sangam.Identity.Infrastructure.Tests.Portal;

/// <summary>The portal's reads and writes through the real DI graph against PostgreSQL.</summary>
[Collection("postgres")]
public sealed class PortalServiceTests : IAsyncLifetime
{
    private static readonly RegisterUserCommand Ravi = new("Ravi", "Menon", "ravi@example.in", "+919876500077", new DateOnly(1986, 6, 12), Gender.Male, "Kaveri-River-2026!", "v1", "203.0.113.7", "xunit");
    private readonly PostgresFixture _pg;
    private ServiceProvider _provider = null!;
    private Guid _userId;
    private Guid _appId;

    public PortalServiceTests(PostgresFixture pg)
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
            ["Sangam:PasswordHashing:MemoryKiB"] = "8192",
            ["Sangam:PasswordHashing:Iterations"] = "2",
            ["Sangam:Otp:ResendCooldown"] = "00:00:00",
            ["Sangam:Maintenance:Enabled"] = "false",
        }).Build();
        services.AddSingleton(configuration);
        services.AddSangamInfrastructure(configuration);
        _provider = services.BuildServiceProvider();

        using IServiceScope scope = _provider.CreateScope();
        IAccountService accounts = scope.ServiceProvider.GetRequiredService<IAccountService>();
        InMemoryEmailOutbox outbox = scope.ServiceProvider.GetRequiredService<InMemoryEmailOutbox>();
        _userId = (await accounts.RegisterAsync(Ravi)).UserId!.Value;
        string code = System.Text.RegularExpressions.Regex.Match(outbox.LatestFor(Ravi.Email)!.Message.TextBody, "[0-9]{6}").Value;
        await accounts.VerifyCodeAsync(_userId, OneTimeCodePurpose.EmailVerification, code, null);

        await using SangamDbContext db = _pg.CreateContext();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        App app = new() { Id = Guid.NewGuid(), ClientId = "his", Slug = "his", DisplayName = "LiPi HIS", OwnerCompanyName = "imagiQa", CreatedAt = now, UpdatedAt = now };
        db.Apps.Add(app);
        db.AppGrants.Add(new AppGrant { Id = Guid.NewGuid(), UserId = _userId, AppId = app.Id, GrantedAt = now.AddDays(-10) });
        db.Consents.Add(new Domain.Entities.Consent { Id = Guid.NewGuid(), UserId = _userId, AppId = app.Id, Scope = "openid profile email", ConsentVersion = "v1", GrantedAt = now.AddDays(-10) });
        await db.SaveChangesAsync();
        _appId = app.Id;
    }

    public async Task DisposeAsync()
    {
        if (_provider is not null)
        {
            await _provider.DisposeAsync();
        }
    }

    [PostgresFact]
    public async Task Overview_CountsAppsSessionsAndSuggestsTwoFactor()
    {
        using IServiceScope scope = _provider.CreateScope();
        IPortalService portal = scope.ServiceProvider.GetRequiredService<IPortalService>();
        ISessionService sessions = scope.ServiceProvider.GetRequiredService<ISessionService>();
        await sessions.StartAsync(_userId, SignInMode.Password, null, null, "203.0.113.7", "Mozilla/5.0 (Windows NT 10.0) Chrome/131.0.0.0");

        PortalOverview overview = await portal.GetOverviewAsync(_userId);

        Assert.Equal(1, overview.LinkedApps);
        Assert.Equal(0, overview.Organisations);
        Assert.Equal(1, overview.ActiveSessions);
        Assert.True(overview.TwoFactorSuggested);
    }

    [PostgresFact]
    public async Task LinkedApps_CarryScopesAndAStatusThatAgesFromLastUse()
    {
        using IServiceScope scope = _provider.CreateScope();
        IPortalService portal = scope.ServiceProvider.GetRequiredService<IPortalService>();

        IReadOnlyList<LinkedApp> apps = await portal.GetLinkedAppsAsync(_userId);

        LinkedApp app = Assert.Single(apps);
        Assert.Equal("LiPi HIS", app.DisplayName);
        Assert.Equal("his", app.ClientId);
        Assert.Equal(["email", "openid", "profile"], app.Scopes);
        Assert.Null(app.LastUsedAt);
        Assert.Equal(LinkedAppStatus.Active, app.StatusAt(app.LinkedAt.AddDays(5)));
        Assert.Equal(LinkedAppStatus.Expiring, app.StatusAt(app.LinkedAt.AddDays(45)));
        Assert.Equal(LinkedAppStatus.Dormant, app.StatusAt(app.LinkedAt.AddDays(120)));
    }

    [PostgresFact]
    public async Task RevokeApp_EndsTheGrantAndConsent_AndIsAudited()
    {
        using IServiceScope scope = _provider.CreateScope();
        IPortalService portal = scope.ServiceProvider.GetRequiredService<IPortalService>();

        Assert.True(await portal.RevokeAppAsync(_userId, _appId, "203.0.113.7"));
        Assert.False(await portal.RevokeAppAsync(_userId, _appId, null));
        Assert.False(await portal.RevokeAppAsync(_userId, Guid.NewGuid(), null));

        Assert.Empty(await portal.GetLinkedAppsAsync(_userId));
        await using SangamDbContext db = _pg.CreateContext();
        Assert.All(await db.AppGrants.Where(g => g.UserId == _userId).ToListAsync(), g => Assert.NotNull(g.RevokedAt));
        Assert.All(await db.Consents.Where(c => c.UserId == _userId).ToListAsync(), c => Assert.NotNull(c.RevokedAt));
        Assert.Contains(await db.AuditEvents.Select(e => e.Action).ToListAsync(), a => a == AuditActions.AppAccessRevoke);
    }

    [PostgresFact]
    public async Task Sessions_ListLiveOnes_MarkTheCurrent_AndRevokeOneOrAll()
    {
        using IServiceScope scope = _provider.CreateScope();
        IPortalService portal = scope.ServiceProvider.GetRequiredService<IPortalService>();
        ISessionService sessions = scope.ServiceProvider.GetRequiredService<ISessionService>();

        Guid desk = await sessions.StartAsync(_userId, SignInMode.Password, _appId, "First Floor Radiology", "10.4.2.37", "Mozilla/5.0 (Windows NT 10.0) Chrome/131.0.0.0");
        Guid phone = await sessions.StartAsync(_userId, SignInMode.PasswordAndOtp, null, null, "203.0.113.9", "Mozilla/5.0 (iPhone; CPU iPhone OS 17_0 like Mac OS X) Safari/604.1");

        IReadOnlyList<PortalSession> live = await portal.GetSessionsAsync(_userId, desk);
        Assert.Equal(2, live.Count);
        Assert.True(live[0].IsCurrent);
        Assert.Equal("First Floor Radiology", live[0].DeviceLabel);
        Assert.Equal("Chrome on Windows", live[0].Browser);
        Assert.Equal("10.4.2.37", live[0].IpAddress);
        Assert.Equal("LiPi HIS", live[0].AppName);
        Assert.Contains(live, s => s.Browser == "Safari on iOS" && s.DeviceLabel is null);

        Assert.True(await portal.RevokeSessionAsync(_userId, phone, null));
        Assert.False(await portal.RevokeSessionAsync(_userId, phone, null));
        Assert.False(await portal.RevokeSessionAsync(Guid.NewGuid(), desk, null));
        Assert.Single(await portal.GetSessionsAsync(_userId, desk));

        // A revoked session no longer validates, so its cookie is rejected on the next check.
        Assert.False(await sessions.TouchAsync(phone, _userId));
        Assert.True(await sessions.TouchAsync(desk, _userId));

        await portal.RevokeAllSessionsAsync(_userId, null);
        Assert.Empty(await portal.GetSessionsAsync(_userId, desk));
        Assert.False(await sessions.TouchAsync(desk, _userId));
    }

    [PostgresFact]
    public async Task RevokeAllSessions_RotatesTheSecurityStamp()
    {
        using IServiceScope scope = _provider.CreateScope();
        IPortalService portal = scope.ServiceProvider.GetRequiredService<IPortalService>();
        IAccountService accounts = scope.ServiceProvider.GetRequiredService<IAccountService>();
        string before = (await accounts.FindByIdAsync(_userId))!.SecurityStamp;

        await portal.RevokeAllSessionsAsync(_userId, null);

        Assert.NotEqual(before, (await accounts.FindByIdAsync(_userId))!.SecurityStamp);
    }

    [PostgresFact]
    public async Task Audit_IsNarratedInPlainLanguage_AndPagedByRange()
    {
        using IServiceScope scope = _provider.CreateScope();
        IPortalService portal = scope.ServiceProvider.GetRequiredService<IPortalService>();

        AuditPage page = await portal.GetAuditAsync(_userId, null, 0, 50);

        Assert.True(page.Total >= 3);
        Assert.Contains(page.Lines, l => l.Summary == "You created your Sangam account.");
        Assert.Contains(page.Lines, l => l.Summary == "You verified your email address.");
        Assert.All(page.Lines, l => Assert.Equal("You", l.Actor));
        Assert.All(page.Lines, l => Assert.DoesNotContain('_', l.Summary));

        AuditPage small = await portal.GetAuditAsync(_userId, null, 0, 2);
        Assert.Equal(2, small.Lines.Count);
        Assert.Equal(page.Total, small.Total);
        Assert.True(small.PageCount >= 2);

        AuditPage future = await portal.GetAuditAsync(_userId, DateTimeOffset.UtcNow.AddMinutes(5), 0, 50);
        Assert.Equal(0, future.Total);
    }

    [PostgresFact]
    public async Task Export_ContainsTheUsersDataAndNoPasswordHash()
    {
        using IServiceScope scope = _provider.CreateScope();
        IPortalService portal = scope.ServiceProvider.GetRequiredService<IPortalService>();
        ISessionService sessions = scope.ServiceProvider.GetRequiredService<ISessionService>();
        await sessions.StartAsync(_userId, SignInMode.Password, null, null, "203.0.113.7", "Mozilla/5.0 (Windows NT 10.0) Chrome/131.0.0.0");

        string json = await portal.ExportAsync(_userId, "203.0.113.7");

        Assert.Contains("\"ravi@example.in\"", json, StringComparison.Ordinal);
        Assert.Contains("\"+919876500077\"", json, StringComparison.Ordinal);
        Assert.Contains("\"LiPi HIS\"", json, StringComparison.Ordinal);
        Assert.Contains("You created your Sangam account.", json, StringComparison.Ordinal);
        Assert.DoesNotContain("argon2", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PasswordHash", json, StringComparison.OrdinalIgnoreCase);

        await using SangamDbContext db = _pg.CreateContext();
        Assert.Contains(await db.AuditEvents.Select(e => e.Action).ToListAsync(), a => a == AuditActions.UserDataExport);
    }

    [PostgresFact]
    public async Task Deletion_StartsAGrace_BlocksSignIn_RevokesApps_AndCanBeCancelled()
    {
        using IServiceScope scope = _provider.CreateScope();
        IPortalService portal = scope.ServiceProvider.GetRequiredService<IPortalService>();
        IAccountService accounts = scope.ServiceProvider.GetRequiredService<IAccountService>();

        Assert.False((await portal.GetDeletionStateAsync(_userId)).Requested);

        await portal.RequestDeletionAsync(_userId, "203.0.113.7");

        DeletionState state = await portal.GetDeletionStateAsync(_userId);
        Assert.True(state.Requested);
        Assert.False(state.OnHold);
        Assert.InRange(state.PurgeAfter!.Value, DateTimeOffset.UtcNow.AddDays(29), DateTimeOffset.UtcNow.AddDays(31));
        Assert.Empty(await portal.GetLinkedAppsAsync(_userId));
        Assert.Equal(SignInStatus.NotAllowed, (await accounts.CheckPasswordAsync(Ravi.Email, Ravi.Password, null, null, null)).Status);

        await portal.CancelDeletionAsync(_userId, null);
        Assert.False((await portal.GetDeletionStateAsync(_userId)).Requested);
        Assert.Equal(SignInStatus.Succeeded, (await accounts.CheckPasswordAsync(Ravi.Email, Ravi.Password, null, null, null)).Status);
    }

    [PostgresFact]
    public async Task Purge_DestroysPersonalDataAfterTheGrace_SkipsHolds_AndKeepsTheAuditTrail()
    {
        using IServiceScope scope = _provider.CreateScope();
        IPortalService portal = scope.ServiceProvider.GetRequiredService<IPortalService>();
        AccountPurgeService purge = _provider.GetServices<Microsoft.Extensions.Hosting.IHostedService>().OfType<AccountPurgeService>().Single();
        await portal.RequestDeletionAsync(_userId, null);

        // Nothing is due yet.
        Assert.Equal(0, (await purge.RunOnceAsync()).Accounts);

        // Bring the purge date forward, then put the account on hold: the sweep must skip it.
        await using (SangamDbContext db = _pg.CreateContext())
        {
            await db.Users.Where(u => u.Id == _userId)
                .ExecuteUpdateAsync(u => u
                    .SetProperty(x => x.PurgeAfter, DateTimeOffset.UtcNow.AddMinutes(-1))
                    .SetProperty(x => x.HoldPlacedAt, DateTimeOffset.UtcNow)
                    .SetProperty(x => x.HoldReason, "Court order 42/2026"));
        }

        Assert.Equal(0, (await purge.RunOnceAsync()).Accounts);
        Assert.True((await portal.GetDeletionStateAsync(_userId)).OnHold);

        await using (SangamDbContext db = _pg.CreateContext())
        {
            await db.Users.Where(u => u.Id == _userId).ExecuteUpdateAsync(u => u.SetProperty(x => x.HoldPlacedAt, (DateTimeOffset?)null));
        }

        Assert.Equal(1, (await purge.RunOnceAsync()).Accounts);

        await using SangamDbContext after = _pg.CreateContext();
        SangamUser purged = await after.Users.SingleAsync(u => u.Id == _userId);
        Assert.Equal(UserStatus.DeletedHard, purged.Status);
        Assert.Equal("Deleted", purged.FirstName);
        Assert.Null(purged.PasswordHash);
        Assert.Null(purged.PhoneNumber);
        Assert.DoesNotContain("ravi@example.in", purged.Email, StringComparison.OrdinalIgnoreCase);
        Assert.Null(purged.PurgeAfter);
        Assert.Contains(await after.AuditEvents.Select(e => e.Action).ToListAsync(), a => a == AuditActions.UserAccountDeletionComplete);
        Assert.True(await after.AuditEvents.AnyAsync(e => e.Action == AuditActions.UserRegister));
    }

    [PostgresFact]
    public async Task Purge_RemovesLongRevokedSessionRowsOnly()
    {
        using IServiceScope scope = _provider.CreateScope();
        ISessionService sessions = scope.ServiceProvider.GetRequiredService<ISessionService>();
        AccountPurgeService purge = _provider.GetServices<Microsoft.Extensions.Hosting.IHostedService>().OfType<AccountPurgeService>().Single();

        Guid live = await sessions.StartAsync(_userId, SignInMode.Password, null, null, "203.0.113.7", "Chrome");
        Guid old = await sessions.StartAsync(_userId, SignInMode.Password, null, null, "203.0.113.8", "Chrome");
        await sessions.EndAsync(old, "user");

        await using (SangamDbContext db = _pg.CreateContext())
        {
            await db.UserSessions.Where(s => s.Id == old).ExecuteUpdateAsync(u => u.SetProperty(x => x.RevokedAt, DateTimeOffset.UtcNow.AddDays(-120)));
        }

        Assert.Equal(1, (await purge.RunOnceAsync()).Sessions);

        await using SangamDbContext after = _pg.CreateContext();
        Assert.True(await after.UserSessions.AnyAsync(s => s.Id == live));
        Assert.False(await after.UserSessions.AnyAsync(s => s.Id == old));
    }

    [PostgresFact]
    public async Task UpdateProfile_ChangesWhatTheUserMayChange_AndLeavesEmailAndBirthDateAlone()
    {
        using IServiceScope scope = _provider.CreateScope();
        IPortalService portal = scope.ServiceProvider.GetRequiredService<IPortalService>();
        IAccountService accounts = scope.ServiceProvider.GetRequiredService<IAccountService>();
        UserSummary before = (await accounts.FindByIdAsync(_userId))!;

        ProfileUpdateResult result = await portal.UpdateProfileAsync(
            _userId,
            new ProfileUpdate("  Ravikumar ", " Menon ", "ta-IN", Gender.Other, "IN", "98765 00078"),
            "203.0.113.7");

        Assert.True(result.Succeeded, result.Message);
        Assert.True(result.MobileChanged);

        UserSummary after = (await accounts.FindByIdAsync(_userId))!;
        Assert.Equal("Ravikumar", after.FirstName);
        Assert.Equal("ta-IN", after.Locale);
        Assert.Equal(Gender.Other, after.Gender);
        Assert.Equal("+919876500078", after.Mobile);
        Assert.False(after.MobileVerified);
        Assert.Equal(before.Email, after.Email);
        Assert.Equal(before.DateOfBirth, after.DateOfBirth);
        Assert.True(after.UpdatedAt > before.UpdatedAt, "updated_at must move so applications can notice.");
    }

    [PostgresFact]
    public async Task UpdateProfile_RejectsEmptyNames_BadNumberLength_AndANumberOnAnotherAccount()
    {
        using IServiceScope scope = _provider.CreateScope();
        IPortalService portal = scope.ServiceProvider.GetRequiredService<IPortalService>();
        IAccountService accounts = scope.ServiceProvider.GetRequiredService<IAccountService>();

        ProfileUpdate valid = new("Ravi", "Menon", "en-IN", Gender.Male, "IN", "9876500077");
        Assert.Equal("FirstName", (await portal.UpdateProfileAsync(_userId, valid with { FirstName = " " }, null)).Field);
        Assert.Equal("LastName", (await portal.UpdateProfileAsync(_userId, valid with { LastName = "" }, null)).Field);
        Assert.Equal("MobileNumber", (await portal.UpdateProfileAsync(_userId, valid with { MobileNumber = "98765" }, null)).Field);
        Assert.Equal("MobileNumber", (await portal.UpdateProfileAsync(_userId, valid with { MobileNumber = "" }, null)).Field);

        // A nine-digit Emirati number is fine for AE and wrong for IN.
        Assert.True((await portal.UpdateProfileAsync(_userId, valid with { CountryIso = "AE", MobileNumber = "501234567" }, null)).Succeeded);

        Guid other = (await accounts.RegisterAsync(Ravi with { Email = "second@example.in", Mobile = "+919876500099" })).UserId!.Value;
        Assert.NotEqual(Guid.Empty, other);
        ProfileUpdateResult clash = await portal.UpdateProfileAsync(_userId, valid with { MobileNumber = "9876500099" }, null);
        Assert.False(clash.Succeeded);
        Assert.Equal("MobileNumber", clash.Field);
    }

    [PostgresFact]
    public async Task UpdateProfile_UnchangedMobile_IsNotMarkedUnverified()
    {
        using IServiceScope scope = _provider.CreateScope();
        IPortalService portal = scope.ServiceProvider.GetRequiredService<IPortalService>();
        IAccountService accounts = scope.ServiceProvider.GetRequiredService<IAccountService>();

        await using (SangamDbContext db = _pg.CreateContext())
        {
            await db.Users.Where(u => u.Id == _userId).ExecuteUpdateAsync(u => u.SetProperty(x => x.PhoneNumberConfirmed, true));
        }

        ProfileUpdateResult result = await portal.UpdateProfileAsync(
            _userId,
            new ProfileUpdate("Ravi", "Menon", "en-IN", Gender.Male, "IN", "98765 00077"),
            null);

        Assert.True(result.Succeeded, result.Message);
        Assert.False(result.MobileChanged);
        Assert.True((await accounts.FindByIdAsync(_userId))!.MobileVerified);
    }
}
