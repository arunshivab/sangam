using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Sangam.Identity.Application.Accounts;
using Sangam.Identity.Application.Admin;
using Sangam.Identity.Application.Partners;
using Sangam.Identity.Application.Portal;
using Sangam.Identity.Application.Tenancy;
using Sangam.Identity.Domain;
using Sangam.Identity.Domain.Entities;
using Sangam.Identity.Domain.Enums;
using Sangam.Identity.Infrastructure.Partners;
using Sangam.Identity.Infrastructure.Persistence;
using Sangam.Identity.Infrastructure.Services;
using Sangam.Identity.Infrastructure.Tests.Postgres;

namespace Sangam.Identity.Infrastructure.Tests.Partners;

/// <summary>
/// The partner console's rules, enforced in <see cref="IPartnerService"/> and proved here against
/// PostgreSQL: scope to one application, the linked-users-only privacy boundary, owner-only
/// administrator management, "stricter only" sign-in policy, and audit attribution to a person.
/// </summary>
[Collection("postgres")]
public sealed class PartnerServiceTests : IAsyncLifetime
{
    private readonly List<IServiceScope> _scopes = [];
    private readonly PostgresFixture _pg;
    private ServiceProvider _provider = null!;
    private Guid _his;
    private Guid _other;
    private Guid _owner;
    private Guid _admin;
    private Guid _nurse;
    private Guid _stranger;
    private Guid _otherAppsUser;
    private Guid _hospital;

    public PartnerServiceTests(PostgresFixture pg)
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

        _owner = await NewUserAsync("owner@lipi.test", "+919876501001");
        _admin = await NewUserAsync("admin@lipi.test", "+919876501002");
        _nurse = await NewUserAsync("nurse@lipi.test", "+919876501003");
        _stranger = await NewUserAsync("stranger@elsewhere.test", "+919876501004");
        _otherAppsUser = await NewUserAsync("other@elsewhere.test", "+919876501005");

        DateTimeOffset now = DateTimeOffset.UtcNow;
        await using SangamDbContext db = _pg.CreateContext();
        App his = new() { Id = Guid.NewGuid(), ClientId = "lipi-his", Slug = "lipi-his", DisplayName = "LiPi HIS", OwnerCompanyName = "imagiQa", CreatedAt = now, UpdatedAt = now };
        App other = new() { Id = Guid.NewGuid(), ClientId = "other", Slug = "other", DisplayName = "Other App", OwnerCompanyName = "Someone", CreatedAt = now, UpdatedAt = now };
        db.Apps.AddRange(his, other);
        foreach (Guid linked in new[] { _owner, _admin, _nurse })
        {
            db.AppGrants.Add(new AppGrant { Id = Guid.NewGuid(), AppId = his.Id, UserId = linked, GrantedAt = now });
        }

        db.AppGrants.Add(new AppGrant { Id = Guid.NewGuid(), AppId = other.Id, UserId = _otherAppsUser, GrantedAt = now });
        db.AppAdmins.AddRange(
            new AppAdmin { Id = Guid.NewGuid(), AppId = his.Id, UserId = _owner, Role = AppAdminRole.Owner, GrantedAt = now },
            new AppAdmin { Id = Guid.NewGuid(), AppId = his.Id, UserId = _admin, Role = AppAdminRole.Admin, GrantedAt = now });
        await db.SaveChangesAsync();
        _his = his.Id;
        _other = other.Id;

        _hospital = Guid.NewGuid();
        PartnerResult created = await Service().UpsertOrganisationAsync(_owner, _his, _hospital, new OrganisationUpsert("Apulki Hospital", "hospital", null, null));
        Assert.True(created.Succeeded, created.Message);
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

    // ------------------------------------------------------------ scope to one application

    [PostgresFact]
    public async Task AnAdmin_SeesOnlyTheApplicationsTheyAdminister()
    {
        PartnerAppRow app = Assert.Single(await Service().GetMyAppsAsync(_admin));
        Assert.Equal("LiPi HIS", app.DisplayName);
        Assert.Equal(AppAdminRole.Admin, app.Role);
        Assert.Empty(await Service().GetMyAppsAsync(_nurse));
    }

    [PostgresFact]
    public async Task NoOne_CanTouchAnApplicationTheyDoNotAdminister()
    {
        IPartnerService partner = Service();
        Assert.Empty(await partner.ListRolesAsync(_owner, _other));
        Assert.Empty(await partner.ListOrganisationsAsync(_owner, _other));
        Assert.Empty(await partner.SearchLinkedUsersAsync(_owner, _other, null, 50));
        Assert.False((await partner.UpsertRoleAsync(_owner, _other, "doctor", new RoleUpsert("Doctor", null, [], null))).Succeeded);
        Assert.Null(await partner.GetSettingsAsync(_owner, _other));
        Assert.False((await partner.UpsertRoleAsync(_nurse, _his, "doctor", new RoleUpsert("Doctor", null, [], null))).Succeeded);
    }

    // ------------------------------------------------------------ the privacy boundary

    [PostgresFact]
    public async Task Search_SeesOnlyPeopleWhoLinkedThisApplication_NeverTheWiderDirectory()
    {
        IReadOnlyList<LinkedUserRow> everyone = await Service().SearchLinkedUsersAsync(_admin, _his, null, 50);
        Assert.Equal(3, everyone.Count);
        Assert.DoesNotContain(everyone, u => u.UserId == _stranger);
        Assert.DoesNotContain(everyone, u => u.UserId == _otherAppsUser);

        // Even an exact email of someone on Sangam finds nothing if they never linked this app.
        Assert.Empty(await Service().SearchLinkedUsersAsync(_admin, _his, "stranger@elsewhere.test", 50));
        Assert.Single(await Service().SearchLinkedUsersAsync(_admin, _his, "nurse@lipi", 50));
    }

    [PostgresFact]
    public async Task ARole_CanOnlyBeGivenToSomeoneWhoLinkedTheApplication_AndIsAttributedToThePerson()
    {
        IPartnerService partner = Service();
        Assert.True((await partner.UpsertRoleAsync(_admin, _his, "nurse", new RoleUpsert("Nurse", null, ["patient:read"], null))).Succeeded);

        PartnerResult refused = await partner.GrantMembershipAsync(_admin, _his, _hospital, _stranger, new MembershipUpsert("nurse", false));
        Assert.False(refused.Succeeded);
        Assert.Contains("has not linked", refused.Message, StringComparison.Ordinal);

        await using (SangamDbContext db = _pg.CreateContext())
        {
            Assert.False(await db.AppGrants.AnyAsync(g => g.AppId == _his && g.UserId == _stranger), "a refused grant must not link the person either");
        }

        Assert.True((await partner.GrantMembershipAsync(_admin, _his, _hospital, _nurse, new MembershipUpsert("nurse", true))).Succeeded);
        PartnerMemberRow member = Assert.Single(await partner.ListMembersAsync(_admin, _his, _hospital));
        Assert.Equal("nurse", member.Role);
        Assert.Equal("nurse@lipi.test", member.Email);

        await using (SangamDbContext db = _pg.CreateContext())
        {
            OrgMembership row = await db.OrgMemberships.SingleAsync(m => m.UserId == _nurse);
            Assert.Equal(_admin, row.GrantedByUserId);
            AuditEvent audit = await db.AuditEvents.SingleAsync(e => e.Action == AuditActions.OrgMembershipGrant);
            Assert.Equal(AuditActorType.Admin, audit.ActorType);
            Assert.Equal(_admin, audit.ActorUserId);
            Assert.Equal(_his, audit.ActorAppId);
        }

        Assert.True((await partner.RevokeMembershipAsync(_admin, _his, _hospital, _nurse)).Succeeded);
        await using (SangamDbContext db = _pg.CreateContext())
        {
            Assert.Equal(_admin, (await db.OrgMemberships.SingleAsync(m => m.UserId == _nurse)).RevokedByUserId);
        }
    }

    [PostgresFact]
    public async Task TheMachineApi_IsStillRecordedAsTheApplication()
    {
        using IServiceScope scope = _provider.CreateScope();
        IManagementService management = scope.ServiceProvider.GetRequiredService<IManagementService>();
        await management.UpsertRoleAsync(_his, "clerk", new RoleUpsert("Clerk", null, [], null), ManagementActor.Api);

        await using SangamDbContext db = _pg.CreateContext();
        AuditEvent row = await db.AuditEvents.Where(e => e.Action == AuditActions.RoleUpsert).OrderByDescending(e => e.OccurredAt).FirstAsync();
        Assert.Equal(AuditActorType.Api, row.ActorType);
        Assert.Null(row.ActorUserId);
    }

    // ------------------------------------------------------------ administrators

    [PostgresFact]
    public async Task OnlyAnOwner_ManagesAdministrators()
    {
        IPartnerService partner = Service();
        Assert.Empty(await partner.ListAdminsAsync(_admin, _his));
        Assert.False((await partner.GrantAdminAsync(_admin, _his, "nurse@lipi.test", AppAdminRole.Admin)).Succeeded);
        Assert.False((await partner.RevokeAdminAsync(_admin, _his, _owner)).Succeeded);

        Assert.Equal(2, (await partner.ListAdminsAsync(_owner, _his)).Count);
        Assert.True((await partner.GrantAdminAsync(_owner, _his, "nurse@lipi.test", AppAdminRole.Admin)).Succeeded);
        Assert.Equal(AppAdminRole.Admin, await partner.GetRoleAsync(_nurse, _his));
        Assert.True((await partner.RevokeAdminAsync(_owner, _his, _nurse)).Succeeded);
        Assert.Null(await partner.GetRoleAsync(_nurse, _his));
    }

    [PostgresFact]
    public async Task GrantingAnAdministrator_GivesOneAnswerForEveryIneligibleAddress()
    {
        IPartnerService partner = Service();
        PartnerResult unknown = await partner.GrantAdminAsync(_owner, _his, "nobody@nowhere.test", AppAdminRole.Admin);
        PartnerResult notLinked = await partner.GrantAdminAsync(_owner, _his, "stranger@elsewhere.test", AppAdminRole.Admin);

        Assert.False(unknown.Succeeded);
        Assert.False(notLinked.Succeeded);

        // Same words, so the form cannot reveal whether an address is registered with Sangam.
        Assert.Equal(unknown.Message, notLinked.Message);
    }

    [PostgresFact]
    public async Task TheLastOwner_CannotBeRemovedOrDemoted()
    {
        IPartnerService partner = Service();
        PartnerResult revoke = await partner.RevokeAdminAsync(_owner, _his, _owner);
        Assert.False(revoke.Succeeded);
        Assert.Contains("last owner", revoke.Message, StringComparison.Ordinal);

        PartnerResult demote = await partner.GrantAdminAsync(_owner, _his, "owner@lipi.test", AppAdminRole.Admin);
        Assert.False(demote.Succeeded);
        Assert.Equal(AppAdminRole.Owner, await partner.GetRoleAsync(_owner, _his));

        Assert.True((await partner.GrantAdminAsync(_owner, _his, "admin@lipi.test", AppAdminRole.Owner)).Succeeded);
        Assert.True((await partner.RevokeAdminAsync(_admin, _his, _owner)).Succeeded);
    }

    [PostgresFact]
    public async Task APlatformAppManager_AssignsTheFirstOwner_EvenBeforeTheyLinkTheApp()
    {
        await using (SangamDbContext db = _pg.CreateContext())
        {
            db.PlatformOperators.Add(new PlatformOperator { Id = Guid.NewGuid(), UserId = _stranger, Role = PlatformRole.AppManager, GrantedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }

        using IServiceScope scope = _provider.CreateScope();
        IAdminService admin = scope.ServiceProvider.GetRequiredService<IAdminService>();
        Assert.True((await admin.AssignAppOwnerAsync(_stranger, _other, "other@elsewhere.test", null)).Succeeded);
        Assert.Equal(AppAdminRole.Owner, await Service().GetRoleAsync(_otherAppsUser, _other));
        Assert.Equal(1, Assert.Single(await admin.ListAppsAsync(_stranger), a => a.Id == _other).PartnerOwners);

        // A viewer may not.
        await using (SangamDbContext db = _pg.CreateContext())
        {
            await db.PlatformOperators.Where(o => o.UserId == _stranger).ExecuteUpdateAsync(u => u.SetProperty(o => o.Role, PlatformRole.Viewer));
        }

        Assert.False((await admin.AssignAppOwnerAsync(_stranger, _his, "nurse@lipi.test", null)).Succeeded);
    }

    // ------------------------------------------------------------ Sangam's own applications

    [PostgresFact]
    public async Task APlatformApp_CannotBeGivenOwners_OrDisabled_ButCanBeReEnabled()
    {
        Guid console = await NewPlatformAppAsync();
        await using (SangamDbContext db = _pg.CreateContext())
        {
            db.PlatformOperators.Add(new PlatformOperator { Id = Guid.NewGuid(), UserId = _stranger, Role = PlatformRole.Owner, GrantedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }

        using IServiceScope scope = _provider.CreateScope();
        IAdminService admin = scope.ServiceProvider.GetRequiredService<IAdminService>();

        // Even the highest platform rank is refused.
        AdminResult owner = await admin.AssignAppOwnerAsync(_stranger, console, "owner@lipi.test", null);
        Assert.False(owner.Succeeded);
        Assert.Contains("part of Sangam itself", owner.Message, StringComparison.Ordinal);

        AdminResult disable = await admin.SetAppStatusAsync(_stranger, console, AppStatus.Disabled, null);
        Assert.False(disable.Succeeded);

        await using (SangamDbContext db = _pg.CreateContext())
        {
            App row = await db.Apps.SingleAsync(a => a.Id == console);
            Assert.Equal(AppStatus.Active, row.Status);
            Assert.False(await db.AppAdmins.AnyAsync(a => a.AppId == console));

            // If one were ever disabled some other way, the console can still turn it back on.
            row.Status = AppStatus.Disabled;
            await db.SaveChangesAsync();
        }

        Assert.True((await admin.SetAppStatusAsync(_stranger, console, AppStatus.Active, null)).Succeeded);
        Assert.True(Assert.Single(await admin.ListAppsAsync(_stranger), a => a.Id == console).IsPlatform);
    }

    [PostgresFact]
    public async Task APlatformApp_IsInvisibleToThePartnerConsole_EvenWithAnAdministratorRow()
    {
        Guid console = await NewPlatformAppAsync();
        await using (SangamDbContext db = _pg.CreateContext())
        {
            db.AppGrants.Add(new AppGrant { Id = Guid.NewGuid(), AppId = console, UserId = _owner, GrantedAt = DateTimeOffset.UtcNow });
            db.AppAdmins.Add(new AppAdmin { Id = Guid.NewGuid(), AppId = console, UserId = _owner, Role = AppAdminRole.Owner, GrantedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }

        IPartnerService partner = Service();
        Assert.DoesNotContain(await partner.GetMyAppsAsync(_owner), a => a.AppId == console);
        Assert.Null(await partner.GetRoleAsync(_owner, console));
        Assert.Null(await partner.GetSettingsAsync(_owner, console));
        Assert.False((await partner.UpdateSettingsAsync(_owner, console, null, "#000000", "S", SignInPolicy.PasswordAndOtp)).Succeeded);
        Assert.False((await partner.UpsertRoleAsync(_owner, console, "x", new RoleUpsert("X", null, [], null))).Succeeded);
    }

    // ------------------------------------------------------------ settings

    [Theory]
    [InlineData(SignInPolicy.Default, SignInPolicy.PasswordAndOtp, true)]
    [InlineData(SignInPolicy.PasswordAndOtp, SignInPolicy.Default, true)]
    [InlineData(SignInPolicy.Default, SignInPolicy.Password, false)]
    [InlineData(SignInPolicy.Default, SignInPolicy.OtpOnly, false)]
    [InlineData(SignInPolicy.PasswordAndOtp, SignInPolicy.Password, false)]
    [InlineData(SignInPolicy.Password, SignInPolicy.PasswordAndOtp, true)]
    [InlineData(SignInPolicy.Password, SignInPolicy.Default, false)]
    [InlineData(SignInPolicy.OtpOnly, SignInPolicy.Default, false)]
    [InlineData(SignInPolicy.OtpOnly, SignInPolicy.OtpOnly, true)]
    public void SignInPolicy_MayOnlyBeTightened_OrReturnedToEachPersonsChoice(SignInPolicy from, SignInPolicy to, bool allowed)
    {
        Assert.Equal(allowed, EfPartnerService.MayMoveTo(from, to));
    }

    [PostgresFact]
    public async Task Settings_SaveBrandingAndATighterPolicy_RefuseAWeakerOne_AndAreAudited()
    {
        IPartnerService partner = Service();
        Assert.True((await partner.UpdateSettingsAsync(_admin, _his, "Hospital information system", "#1d4e89", "L", SignInPolicy.PasswordAndOtp)).Succeeded);

        PartnerAppSettings settings = (await partner.GetSettingsAsync(_admin, _his))!;
        Assert.Equal("#1D4E89", settings.BrandColour);
        Assert.Equal(SignInPolicy.PasswordAndOtp, settings.SignInPolicy);

        PartnerResult weaker = await partner.UpdateSettingsAsync(_admin, _his, null, "#1D4E89", "L", SignInPolicy.Password);
        Assert.False(weaker.Succeeded);
        Assert.Equal(SignInPolicy.PasswordAndOtp, (await partner.GetSettingsAsync(_admin, _his))!.SignInPolicy);

        Assert.False((await partner.UpdateSettingsAsync(_admin, _his, null, "blue", "L", SignInPolicy.Default)).Succeeded);
        Assert.False((await partner.UpdateSettingsAsync(_admin, _his, null, "#1D4E89", "LPH", SignInPolicy.Default)).Succeeded);

        await using SangamDbContext db = _pg.CreateContext();
        AuditEvent row = await db.AuditEvents.SingleAsync(e => e.Action == AuditActions.AppSettingsUpdate);
        Assert.Equal(_admin, row.ActorUserId);
        Assert.Contains("PasswordAndOtp", row.Metadata, StringComparison.Ordinal);
    }

    [PostgresFact]
    public async Task ThePersonGivenARole_SeesWhichApplicationsAdministratorDidIt()
    {
        IPartnerService partner = Service();
        await partner.UpsertRoleAsync(_admin, _his, "nurse", new RoleUpsert("Nurse", null, [], null));
        await partner.GrantMembershipAsync(_admin, _his, _hospital, _nurse, new MembershipUpsert("nurse", false));

        using IServiceScope scope = _provider.CreateScope();
        AuditPage page = await scope.ServiceProvider.GetRequiredService<IPortalService>().GetAuditAsync(_nurse, null, 0, 50);
        AuditLine line = Assert.Single(page.Lines, l => l.Action == AuditActions.OrgMembershipGrant);

        // A person did it, and it was LiPi's own staff — not the application, not a Sangam operator.
        Assert.StartsWith("An administrator of LiPi HIS gave you a role", line.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("Sangam operator", line.Summary, StringComparison.Ordinal);
    }

    [PostgresFact]
    public async Task BeingMadeAnAdministrator_IsNarratedByWhoDidIt()
    {
        await Service().GrantAdminAsync(_owner, _his, "nurse@lipi.test", AppAdminRole.Admin);
        await using (SangamDbContext db = _pg.CreateContext())
        {
            db.PlatformOperators.Add(new PlatformOperator { Id = Guid.NewGuid(), UserId = _stranger, Role = PlatformRole.AppManager, GrantedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }

        using IServiceScope scope = _provider.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IAdminService>().AssignAppOwnerAsync(_stranger, _other, "other@elsewhere.test", null);
        IPortalService portal = scope.ServiceProvider.GetRequiredService<IPortalService>();

        AuditLine byPartner = Assert.Single((await portal.GetAuditAsync(_nurse, null, 0, 50)).Lines, l => l.Action == AuditActions.AppAdminGrant);
        Assert.Equal("An administrator of LiPi HIS made you an administrator of LiPi HIS.", byPartner.Summary);
        Assert.True(byPartner.IsSecuritySensitive);

        AuditLine byOperator = Assert.Single((await portal.GetAuditAsync(_otherAppsUser, null, 0, 50)).Lines, l => l.Action == AuditActions.AppAdminGrant);
        Assert.Equal("A Sangam operator made you an owner of Other App.", byOperator.Summary);
    }

    [PostgresFact]
    public async Task TheApplicationsOwnCode_IsStillNarratedAsTheApplication()
    {
        using IServiceScope scope = _provider.CreateScope();
        IManagementService management = scope.ServiceProvider.GetRequiredService<IManagementService>();
        await management.UpsertRoleAsync(_his, "clerk", new RoleUpsert("Clerk", null, [], null), ManagementActor.Api);
        await management.UpsertMembershipAsync(_his, _hospital, _nurse, new MembershipUpsert("clerk", false), ManagementActor.Api);

        AuditPage page = await scope.ServiceProvider.GetRequiredService<IPortalService>().GetAuditAsync(_nurse, null, 0, 50);
        Assert.StartsWith("LiPi HIS gave you a role", Assert.Single(page.Lines, l => l.Action == AuditActions.OrgMembershipGrant).Summary, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------ helpers

    private async Task<Guid> NewPlatformAppAsync()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await using SangamDbContext db = _pg.CreateContext();
        App app = new() { Id = Guid.NewGuid(), ClientId = "sangam-admin", Slug = "console", DisplayName = "Sangam console", OwnerCompanyName = "imagiQa", IsPlatform = true, CreatedAt = now, UpdatedAt = now };
        db.Apps.Add(app);
        await db.SaveChangesAsync();
        return app.Id;
    }

    private IPartnerService Service()
    {
        IServiceScope scope = _provider.CreateScope();
        _scopes.Add(scope);
        return scope.ServiceProvider.GetRequiredService<IPartnerService>();
    }

    private async Task<Guid> NewUserAsync(string email, string mobile)
    {
        using IServiceScope scope = _provider.CreateScope();
        IAccountService accounts = scope.ServiceProvider.GetRequiredService<IAccountService>();
        InMemoryEmailOutbox outbox = scope.ServiceProvider.GetRequiredService<InMemoryEmailOutbox>();
        Guid id = (await accounts.RegisterAsync(new RegisterUserCommand(
            "Test", email.Split('@')[0], email, mobile, new DateOnly(1985, 1, 1), Gender.PreferNotToSay, "Kaveri-River-2026!", "v1", null, null))).UserId!.Value;
        string code = System.Text.RegularExpressions.Regex.Match(outbox.LatestFor(email)!.Message.TextBody, "[0-9]{6}").Value;
        await accounts.VerifyCodeAsync(id, OneTimeCodePurpose.EmailVerification, code, null);
        return id;
    }
}
