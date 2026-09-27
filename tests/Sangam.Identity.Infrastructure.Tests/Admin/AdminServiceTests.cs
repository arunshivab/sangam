using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Sangam.Identity.Application.Accounts;
using Sangam.Identity.Application.Admin;
using Sangam.Identity.Application.Portal;
using Sangam.Identity.Domain;
using Sangam.Identity.Domain.Entities;
using Sangam.Identity.Domain.Enums;
using Sangam.Identity.Infrastructure.Persistence;
using Sangam.Identity.Infrastructure.Services;
using Sangam.Identity.Infrastructure.Tests.Postgres;

namespace Sangam.Identity.Infrastructure.Tests.Admin;

/// <summary>
/// The console's authorisation lives in <see cref="IAdminService"/>, not in the pages, so this is
/// where every rank boundary is proved — one test per line of the authority chart.
/// </summary>
[Collection("postgres")]
public sealed class AdminServiceTests : IAsyncLifetime
{
    private readonly List<IServiceScope> _scopes = [];
    private readonly PostgresFixture _pg;
    private ServiceProvider _provider = null!;
    private Guid _owner;
    private Guid _support;
    private Guid _appManager;
    private Guid _viewer;
    private Guid _patient;
    private Guid _appId;

    public AdminServiceTests(PostgresFixture pg)
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

        _owner = await NewUserAsync("owner@example.in", "+919876500001");
        _support = await NewUserAsync("support@example.in", "+919876500002");
        _appManager = await NewUserAsync("apps@example.in", "+919876500003");
        _viewer = await NewUserAsync("viewer@example.in", "+919876500004");
        _patient = await NewUserAsync("patient@example.in", "+919876500005");

        await using SangamDbContext db = _pg.CreateContext();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        db.PlatformOperators.AddRange(
            new PlatformOperator { Id = Guid.NewGuid(), UserId = _owner, Role = PlatformRole.Owner, GrantedAt = now },
            new PlatformOperator { Id = Guid.NewGuid(), UserId = _support, Role = PlatformRole.Support, GrantedAt = now },
            new PlatformOperator { Id = Guid.NewGuid(), UserId = _appManager, Role = PlatformRole.AppManager, GrantedAt = now },
            new PlatformOperator { Id = Guid.NewGuid(), UserId = _viewer, Role = PlatformRole.Viewer, GrantedAt = now });
        App app = new() { Id = Guid.NewGuid(), ClientId = "his", Slug = "his", DisplayName = "LiPi HIS", OwnerCompanyName = "imagiQa", CreatedAt = now, UpdatedAt = now };
        db.Apps.Add(app);
        await db.SaveChangesAsync();
        _appId = app.Id;
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

    // ---------------------------------------------------------------- reading

    [PostgresFact]
    public async Task EveryRankCanSearch_ButSomeoneWithoutARankSeesNothing()
    {
        IAdminService admin = Service();
        foreach (Guid op in new[] { _viewer, _appManager, _support, _owner })
        {
            Assert.NotEmpty(await admin.SearchUsersAsync(op, "patient", 50));
        }

        Assert.Empty(await admin.SearchUsersAsync(_patient, "patient", 50));
    }

    [PostgresFact]
    public async Task Search_FindsByNameEmailOrMobileDigits()
    {
        IAdminService admin = Service();
        Assert.Single(await admin.SearchUsersAsync(_viewer, "patient@example", 50));
        Assert.Single(await admin.SearchUsersAsync(_viewer, "98765 00005", 50));
        Assert.True((await admin.SearchUsersAsync(_viewer, "Test User", 50)).Count >= 5);
    }

    [PostgresFact]
    public async Task OpeningARecord_IsAudited_AndTheUserCanSeeWhoLooked()
    {
        IAdminService admin = Service();
        AdminUserDetail? detail = await admin.OpenUserAsync(_viewer, _patient, "203.0.113.4");

        Assert.NotNull(detail);
        Assert.Equal("patient@example.in", detail.Row.Email);

        await using SangamDbContext db = _pg.CreateContext();
        AuditEvent read = await db.AuditEvents.SingleAsync(e => e.Action == AuditActions.AdminUserRead);
        Assert.Equal(AuditActorType.Admin, read.ActorType);
        Assert.Equal(_viewer, read.ActorUserId);
        Assert.Equal(_patient, read.TargetId);

        using IServiceScope scope = _provider.CreateScope();
        AuditPage mine = await scope.ServiceProvider.GetRequiredService<IPortalService>().GetAuditAsync(_patient, null, 0, 50);
        AuditLine line = Assert.Single(mine.Lines, l => l.Action == AuditActions.AdminUserRead);
        Assert.Equal("A Sangam operator opened your account record.", line.Summary);
        Assert.True(line.IsSecuritySensitive);
    }

    [PostgresFact]
    public async Task SomeoneWithoutARank_CannotOpenARecord_AndLeavesNoReadRow()
    {
        Assert.Null(await Service().OpenUserAsync(_patient, _viewer, null));
        await using SangamDbContext db = _pg.CreateContext();
        Assert.False(await db.AuditEvents.AnyAsync(e => e.Action == AuditActions.AdminUserRead));
    }

    // ---------------------------------------------------------------- acting on a user

    [PostgresFact]
    public async Task OnlySupportAndAbove_MayActOnAUser()
    {
        IAdminService admin = Service();
        foreach (Guid tooLow in new[] { _viewer, _appManager })
        {
            Assert.False((await admin.SuspendUserAsync(tooLow, _patient, "test", null)).Succeeded);
            Assert.False((await admin.ForceSignOutAsync(tooLow, _patient, null)).Succeeded);
            Assert.False((await admin.PlaceHoldAsync(tooLow, _patient, "test", null)).Succeeded);
        }

        Assert.True((await admin.SuspendUserAsync(_support, _patient, "Reported compromise", null)).Succeeded);
        Assert.True((await admin.ReinstateUserAsync(_support, _patient, null)).Succeeded);
        Assert.True((await admin.ForceSignOutAsync(_support, _patient, null)).Succeeded);
    }

    [PostgresFact]
    public async Task Suspending_BlocksSignIn_EndsSessions_AndRecordsTheReason()
    {
        using IServiceScope scope = _provider.CreateScope();
        ISessionService sessions = scope.ServiceProvider.GetRequiredService<ISessionService>();
        IAccountService accounts = scope.ServiceProvider.GetRequiredService<IAccountService>();
        Guid session = await sessions.StartAsync(_patient, SignInMode.Password, null, null, "10.0.0.1", "Chrome");

        AdminResult result = await Service().SuspendUserAsync(_support, _patient, "Reported compromise", null);

        Assert.True(result.Succeeded);
        Assert.False(await sessions.TouchAsync(session, _patient));
        Assert.Equal(SignInStatus.NotAllowed, (await accounts.CheckPasswordAsync("patient@example.in", "Kaveri-River-2026!", null, null, null)).Status);

        await using SangamDbContext db = _pg.CreateContext();
        AuditEvent row = await db.AuditEvents.SingleAsync(e => e.Action == AuditActions.AdminUserSuspend);
        Assert.Contains("Reported compromise", row.Metadata, StringComparison.Ordinal);
    }

    [PostgresFact]
    public async Task Holds_BlockThePurge_AndNeedSupport()
    {
        IAdminService admin = Service();
        Assert.True((await admin.PlaceHoldAsync(_support, _patient, "Court order 42/2026", null)).Succeeded);

        await using (SangamDbContext db = _pg.CreateContext())
        {
            SangamUser held = await db.Users.SingleAsync(u => u.Id == _patient);
            Assert.NotNull(held.HoldPlacedAt);
            Assert.Equal("Court order 42/2026", held.HoldReason);
        }

        Assert.True((await admin.ClearHoldAsync(_support, _patient, null)).Succeeded);
        Assert.False((await admin.ClearHoldAsync(_support, _patient, null)).Succeeded);
    }

    // ---------------------------------------------------------------- owner-only

    [PostgresFact]
    public async Task DeleteNow_IsOwnerOnly_PseudonymisesAndKeepsTheAuditTrail()
    {
        IAdminService admin = Service();
        Assert.False((await admin.DeleteNowAsync(_support, _patient, "test", null)).Succeeded);

        AdminResult deleted = await admin.DeleteNowAsync(_owner, _patient, "Verified erasure request", null);

        Assert.True(deleted.Succeeded, deleted.Message);
        await using SangamDbContext db = _pg.CreateContext();
        SangamUser gone = await db.Users.SingleAsync(u => u.Id == _patient);
        Assert.Equal(UserStatus.DeletedHard, gone.Status);
        Assert.Null(gone.PasswordHash);
        Assert.DoesNotContain("patient@example.in", gone.Email, StringComparison.OrdinalIgnoreCase);
        Assert.True(await db.AuditEvents.AnyAsync(e => e.Action == AuditActions.UserRegister && e.ActorUserId == _patient));
        Assert.False((await admin.DeleteNowAsync(_owner, _patient, "again", null)).Succeeded);
    }

    [PostgresFact]
    public async Task DeleteNow_RefusesAnAccountThatStillHoldsConsoleAccess()
    {
        AdminResult result = await Service().DeleteNowAsync(_owner, _viewer, "test", null);
        Assert.False(result.Succeeded);
        Assert.Contains("Revoke", result.Message, StringComparison.Ordinal);
    }

    [PostgresFact]
    public async Task OnlyOwners_ListGrantAndRevokeOperators()
    {
        IAdminService admin = Service();
        foreach (Guid notOwner in new[] { _viewer, _appManager, _support })
        {
            Assert.Empty(await admin.ListOperatorsAsync(notOwner));
            Assert.False((await admin.GrantOperatorAsync(notOwner, "patient@example.in", PlatformRole.Viewer, null)).Succeeded);
            Assert.False((await admin.RevokeOperatorAsync(notOwner, _viewer, null)).Succeeded);
        }

        Assert.Equal(4, (await admin.ListOperatorsAsync(_owner)).Count);
        AdminResult granted = await admin.GrantOperatorAsync(_owner, "patient@example.in", PlatformRole.AppManager, null);
        Assert.True(granted.Succeeded);
        Assert.Contains("authenticator", granted.Message, StringComparison.Ordinal);
        Assert.Equal(PlatformRole.AppManager, await admin.GetRoleAsync(_patient));

        Assert.True((await admin.RevokeOperatorAsync(_owner, _patient, null)).Succeeded);
        Assert.Null(await admin.GetRoleAsync(_patient));
    }

    [PostgresFact]
    public async Task Grant_RefusesSomeoneWithoutAVerifiedAccount()
    {
        AdminResult result = await Service().GrantOperatorAsync(_owner, "nobody@example.in", PlatformRole.Viewer, null);
        Assert.False(result.Succeeded);
        Assert.Contains("register first", result.Message, StringComparison.Ordinal);
    }

    [PostgresFact]
    public async Task TheLastOwner_CannotBeRevoked()
    {
        IAdminService admin = Service();
        Guid second = await NewUserAsync("second-owner@example.in", "+919876500009");

        AdminResult last = await admin.RevokeOperatorAsync(_owner, _owner, null);
        Assert.False(last.Succeeded);
        Assert.Contains("last owner", last.Message, StringComparison.Ordinal);

        await admin.GrantOperatorAsync(_owner, "second-owner@example.in", PlatformRole.Owner, null);
        Assert.True((await admin.RevokeOperatorAsync(second, _owner, null)).Succeeded);
    }

    // ---------------------------------------------------------------- applications

    [PostgresFact]
    public async Task AppManagerAndAbove_MayDisableAnApplication_AViewerMayNot()
    {
        IAdminService admin = Service();
        Assert.False((await admin.SetAppStatusAsync(_viewer, _appId, AppStatus.Disabled, null)).Succeeded);
        Assert.True((await admin.SetAppStatusAsync(_appManager, _appId, AppStatus.Disabled, null)).Succeeded);
        Assert.True((await admin.SetAppStatusAsync(_support, _appId, AppStatus.Active, null)).Succeeded);

        IReadOnlyList<AdminAppRow> apps = await admin.ListAppsAsync(_viewer);
        Assert.Equal(AppStatus.Active, Assert.Single(apps, a => a.Id == _appId).Status);
    }

    [PostgresFact]
    public async Task AnAppManager_CanLookAtUsers_ButNeverActOnThem()
    {
        IAdminService admin = Service();
        Assert.NotNull(await admin.OpenUserAsync(_appManager, _patient, null));
        AdminResult refused = await admin.SuspendUserAsync(_appManager, _patient, "test", null);
        Assert.False(refused.Succeeded);
        Assert.Contains("support", refused.Message, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- helpers

    private IAdminService Service()
    {
        IServiceScope scope = _provider.CreateScope();
        _scopes.Add(scope);
        return scope.ServiceProvider.GetRequiredService<IAdminService>();
    }

    private async Task<Guid> NewUserAsync(string email, string mobile)
    {
        using IServiceScope scope = _provider.CreateScope();
        IAccountService accounts = scope.ServiceProvider.GetRequiredService<IAccountService>();
        InMemoryEmailOutbox outbox = scope.ServiceProvider.GetRequiredService<InMemoryEmailOutbox>();
        Guid id = (await accounts.RegisterAsync(new RegisterUserCommand(
            "Test", "User", email, mobile, new DateOnly(1985, 1, 1), Gender.PreferNotToSay, "Kaveri-River-2026!", "v1", null, null))).UserId!.Value;
        string code = System.Text.RegularExpressions.Regex.Match(outbox.LatestFor(email)!.Message.TextBody, "[0-9]{6}").Value;
        await accounts.VerifyCodeAsync(id, OneTimeCodePurpose.EmailVerification, code, null);
        return id;
    }
}
