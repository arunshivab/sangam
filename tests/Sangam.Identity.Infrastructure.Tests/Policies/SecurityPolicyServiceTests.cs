using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Sangam.Identity.Application.Partners;
using Sangam.Identity.Application.Security;
using Sangam.Identity.Application.Tenancy;
using Sangam.Identity.Domain;
using Sangam.Identity.Domain.Entities;
using Sangam.Identity.Domain.Enums;
using Sangam.Identity.Infrastructure.Persistence;
using Sangam.Identity.Infrastructure.Policies;
using Sangam.Identity.Infrastructure.Tests.Accounts;
using Sangam.Identity.Infrastructure.Tests.Postgres;

namespace Sangam.Identity.Infrastructure.Tests.Policies;

/// <summary>
/// PR-16: platform → application → organisation (with its ancestors) policies; stricter only; enforced when a
/// password is set. Tree: Apulki Group (corporate) → Apulki Baner (hospital) → Radiology (department).
/// </summary>
[Collection("postgres")]
public sealed class SecurityPolicyServiceTests : IAsyncLifetime
{
    private readonly PostgresFixture _pg;
    private readonly List<IServiceScope> _scopes = [];
    private readonly FakeBreaches _breaches = new();
    private ServiceProvider _provider = null!;
    private Guid _appId;
    private Guid _otherAppId;
    private Guid _group;
    private Guid _hospital;
    private Guid _radiology;
    private Guid _admin;
    private Guid _nurse;
    private Guid _radiographer;
    private Guid _outsider;

    public SecurityPolicyServiceTests(PostgresFixture pg)
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
            ["Sangam:Maintenance:Enabled"] = "false",
            ["Sangam:DataProtection:PersistKeys"] = "false",
            ["Sangam:Passwords:BreachCheck:Enabled"] = "true",
            ["Sangam:Passwords:BreachCheck:RequiredForEveryone"] = "false",
        }).Build();
        services.AddSingleton(configuration);
        services.AddSangamInfrastructure(configuration);
        services.AddSingleton<IBreachedPasswordChecker>(_breaches);
        _provider = services.BuildServiceProvider();

        await using SangamDbContext db = _pg.CreateContext();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        App app = new() { Id = Guid.NewGuid(), ClientId = "lipi-his", Slug = "lipi-his", DisplayName = "LiPi HIS", OwnerCompanyName = "imagiQa", MinPasswordLength = 14, CreatedAt = now, UpdatedAt = now };
        App other = new() { Id = Guid.NewGuid(), ClientId = "sigma", Slug = "sigma", DisplayName = "Sigma", OwnerCompanyName = "imagiQa", CreatedAt = now, UpdatedAt = now };
        db.Apps.AddRange(app, other);
        SangamUser[] people = [TestUsers.New("admin@example.in", "+919000000101"), TestUsers.New("nurse@example.in", "+919000000102"), TestUsers.New("radio@example.in", "+919000000103"), TestUsers.New("outsider@example.in", "+919000000104")];
        db.Users.AddRange(people);
        db.AppAdmins.Add(new AppAdmin { Id = Guid.NewGuid(), AppId = app.Id, UserId = people[0].Id, Role = AppAdminRole.Owner, GrantedAt = now });
        await db.SaveChangesAsync();
        (_appId, _otherAppId) = (app.Id, other.Id);
        (_admin, _nurse, _radiographer, _outsider) = (people[0].Id, people[1].Id, people[2].Id, people[3].Id);

        IManagementService management = Scope().GetRequiredService<IManagementService>();
        (_group, _hospital, _radiology) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        Guid otherOrg = Guid.NewGuid();
        await management.UpsertRoleAsync(_appId, "staff", new RoleUpsert("Staff", null, ["patient:read"], null), ManagementActor.Api);
        await management.UpsertRoleAsync(_otherAppId, "staff", new RoleUpsert("Staff", null, ["doc:read"], null), ManagementActor.Api);
        await management.UpsertOrganisationAsync(_appId, _group, new OrganisationUpsert("Apulki Group", "corporate", null, null), ManagementActor.Api);
        await management.UpsertOrganisationAsync(_appId, _hospital, new OrganisationUpsert("Apulki Baner", "hospital", _group, null), ManagementActor.Api);
        await management.UpsertOrganisationAsync(_appId, _radiology, new OrganisationUpsert("Radiology", "department", _hospital, null), ManagementActor.Api);
        await management.UpsertOrganisationAsync(_otherAppId, otherOrg, new OrganisationUpsert("Elsewhere", "hospital", null, null), ManagementActor.Api);
        await management.UpsertMembershipAsync(_appId, _hospital, _nurse, new MembershipUpsert("staff", false), ManagementActor.Person(_nurse));
        await management.UpsertMembershipAsync(_appId, _radiology, _radiographer, new MembershipUpsert("staff", false), ManagementActor.Person(_radiographer));
        await management.UpsertMembershipAsync(_otherAppId, otherOrg, _outsider, new MembershipUpsert("staff", false), ManagementActor.Person(_outsider));

        await using SangamDbContext edit = _pg.CreateContext();
        Organisation group = await edit.Organisations.SingleAsync(o => o.Id == _group);
        group.SignInPolicy = SignInPolicy.PasswordAndOtp;
        Organisation hospital = await edit.Organisations.SingleAsync(o => o.Id == _hospital);
        hospital.MinPasswordLength = 16;
        hospital.MfaRequirement = MfaRequirement.RequiredForAdministrators;
        Organisation radiology = await edit.Organisations.SingleAsync(o => o.Id == _radiology);
        radiology.MfaRequirement = MfaRequirement.Required;
        await edit.SaveChangesAsync();
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
    public async Task APerson_GetsTheApplication_TightenedByTheirOrganisation_AndEveryAncestor()
    {
        ISecurityPolicyService policies = Scope().GetRequiredService<ISecurityPolicyService>();

        PersonPolicy nurse = await policies.ForPersonAsync(_nurse, _appId);
        Assert.Equal(new SecurityPolicy(SignInPolicy.PasswordAndOtp, 16, MfaRequirement.RequiredForAdministrators, false), nurse.Policy);
        Assert.False(nurse.RequiresSecondFactor);

        PersonPolicy radiographer = await policies.ForPersonAsync(_radiographer, _appId);
        Assert.Equal(MfaRequirement.Required, radiographer.Policy.Mfa);
        Assert.True(radiographer.RequiresSecondFactor);

        // The application's own administrator has no organisation, so only the application's rules apply.
        PersonPolicy admin = await policies.ForPersonAsync(_admin, _appId);
        Assert.Equal(new SecurityPolicy(SignInPolicy.Default, 14, MfaRequirement.Optional, false), admin.Policy);
        Assert.True(admin.IsAdministrator);

        // Another application's organisations change nothing here, and nothing of this one applies there.
        Assert.Equal(new SecurityPolicy(SignInPolicy.Default, 14, MfaRequirement.Optional, false), (await policies.ForPersonAsync(_outsider, _appId)).Policy);
        Assert.Equal(policies.Platform, (await policies.ForPersonAsync(_nurse, _otherAppId)).Policy);
    }

    [PostgresFact]
    public async Task ARevokedRole_NoLongerBindsThePerson()
    {
        IManagementService management = Scope().GetRequiredService<IManagementService>();
        await management.RevokeMembershipAsync(_appId, _radiology, _radiographer, ManagementActor.Api);
        PersonPolicy after = await Scope().GetRequiredService<ISecurityPolicyService>().ForPersonAsync(_radiographer, _appId);
        Assert.Equal(MfaRequirement.Optional, after.Policy.Mfa);
        Assert.Equal(14, after.Policy.MinPasswordLength);
    }

    [PostgresFact]
    public async Task ANewPassword_MustMeetTheLongestMinimum_AndNotBeBreached()
    {
        IServiceProvider sp = Scope();
        UserManager<SangamUser> users = sp.GetRequiredService<UserManager<SangamUser>>();
        SangamUser nurse = (await users.FindByIdAsync(_nurse.ToString("D")))!;

        IdentityResult tooShort = await users.AddPasswordAsync(nurse, "Kaveri-River-26");
        Assert.Contains(tooShort.Errors, e => e.Code == PolicyPasswordValidator.TooShortCode && e.Description.Contains("16", StringComparison.Ordinal));

        _breaches.Breached.Add("Breached-Password-2026!");
        IdentityResult breached = await users.AddPasswordAsync(nurse, "Breached-Password-2026!");
        Assert.Contains(breached.Errors, e => e.Code == PolicyPasswordValidator.BreachedCode);

        Assert.True((await users.AddPasswordAsync(nurse, "Kaveri-River-2026!")).Succeeded);

        // Someone outside every organisation only meets the platform's twelve.
        SangamUser outsider = (await users.FindByIdAsync(_outsider.ToString("D")))!;
        Assert.True((await users.AddPasswordAsync(outsider, "Kaveri-River-26")).Succeeded);
    }

    [PostgresFact]
    public async Task AnOrganisation_CanOnlyTighten_WhatItInherits()
    {
        IPartnerService partners = Scope().GetRequiredService<IPartnerService>();

        PolicyView? radiology = await partners.GetOrganisationPolicyAsync(_admin, _appId, _radiology);
        Assert.Equal(new SecurityPolicy(SignInPolicy.PasswordAndOtp, 16, MfaRequirement.RequiredForAdministrators, false), radiology!.Inherited);

        Assert.False((await partners.UpdateOrganisationPolicyAsync(_admin, _appId, _radiology, new PolicyInput(null, 14, null, null))).Succeeded);
        Assert.False((await partners.UpdateOrganisationPolicyAsync(_admin, _appId, _radiology, new PolicyInput(SignInPolicy.Password, null, null, null))).Succeeded);
        Assert.False((await partners.UpdateOrganisationPolicyAsync(_admin, _appId, _radiology, new PolicyInput(null, null, MfaRequirement.Optional, null))).Succeeded);
        Assert.False((await partners.UpdateOrganisationPolicyAsync(_nurse, _appId, _radiology, new PolicyInput(SignInPolicy.PasskeyOnly, null, null, null))).Succeeded);

        PartnerResult saved = await partners.UpdateOrganisationPolicyAsync(_admin, _appId, _radiology, new PolicyInput(SignInPolicy.PasskeyOnly, 18, MfaRequirement.Required, true));
        Assert.True(saved.Succeeded, saved.Message);
        Assert.Equal(new SecurityPolicy(SignInPolicy.PasskeyOnly, 18, MfaRequirement.Required, true), (await Scope().GetRequiredService<ISecurityPolicyService>().ForPersonAsync(_radiographer, _appId)).Policy);

        await using SangamDbContext db = _pg.CreateContext();
        AuditEvent audit = await db.AuditEvents.SingleAsync(e => e.Action == AuditActions.OrgPolicyUpdate);
        Assert.Equal(_radiology, audit.TargetId);
        Assert.Contains("PasskeyOnly", audit.Metadata, StringComparison.Ordinal);
    }

    [PostgresFact]
    public async Task TheApplication_CanOnlyTightenThePlatform_AndOnlyItsAdministratorsMayTry()
    {
        IPartnerService partners = Scope().GetRequiredService<IPartnerService>();
        Assert.False((await partners.UpdateAppPolicyAsync(_admin, _appId, new PolicyInput(null, 10, null, null))).Succeeded);
        Assert.False((await partners.UpdateAppPolicyAsync(_nurse, _appId, new PolicyInput(null, 20, null, null))).Succeeded);

        PartnerResult saved = await partners.UpdateAppPolicyAsync(_admin, _appId, new PolicyInput(null, 20, MfaRequirement.RequiredForAdministrators, true));
        Assert.True(saved.Succeeded, saved.Message);
        PolicyView? view = await partners.GetAppPolicyAsync(_admin, _appId);
        Assert.Equal((20, MfaRequirement.RequiredForAdministrators, true), (view!.MinPasswordLength, view.Mfa!.Value, view.BreachedPasswordCheck == true));
        Assert.True((await Scope().GetRequiredService<ISecurityPolicyService>().ForPersonAsync(_admin, _appId)).RequiresSecondFactor);

        // Passkey only is a rule a partner may choose for the whole application.
        PartnerAppSettings settings = (await partners.GetSettingsAsync(_admin, _appId))!;
        Assert.True((await partners.UpdateSettingsAsync(_admin, _appId, settings.Description, settings.BrandColour, settings.Glyph, SignInPolicy.PasskeyOnly)).Succeeded);
        Assert.Equal(SignInPolicy.PasskeyOnly, (await Scope().GetRequiredService<ISecurityPolicyService>().ForAppAsync(_appId)).SignIn);
    }

    private IServiceProvider Scope()
    {
        IServiceScope scope = _provider.CreateScope();
        _scopes.Add(scope);
        return scope.ServiceProvider;
    }

    private sealed class FakeBreaches : IBreachedPasswordChecker
    {
        public HashSet<string> Breached { get; } = new(StringComparer.Ordinal);

        public bool Available => true;

        public Task<bool?> IsBreachedAsync(string password, CancellationToken cancellationToken = default)
            => Task.FromResult<bool?>(Breached.Contains(password));
    }
}
