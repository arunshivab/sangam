using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Sangam.Identity.Application.Accounts;
using Sangam.Identity.Application.Admin;
using Sangam.Identity.Domain;
using Sangam.Identity.Domain.Entities;
using Sangam.Identity.Domain.Enums;
using Sangam.Identity.Infrastructure.Persistence;
using Sangam.Identity.Infrastructure.Services;
using Sangam.Identity.Infrastructure.Tests.Accounts;
using Sangam.Identity.Infrastructure.Tests.Postgres;

namespace Sangam.Identity.Infrastructure.Tests.Admin;

/// <summary>PR-16 (CAP-019) and D-K: support resets a lost authenticator only after proving identity, after a cooling-off period the owner can cancel, or at once under an audited, alerted override.</summary>
[Collection("postgres")]
public sealed class TwoStepResetTests : IAsyncLifetime
{
    private readonly PostgresFixture _pg;
    private readonly List<IServiceScope> _scopes = [];
    private ServiceProvider _provider = null!;
    private Guid _owner;
    private Guid _support;
    private Guid _viewer;
    private Guid _person;

    public TwoStepResetTests(PostgresFixture pg)
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
            ["Sangam:DataProtection:PersistKeys"] = "false",
        }).Build();
        services.AddSingleton(configuration);
        services.AddSangamInfrastructure(configuration);
        _provider = services.BuildServiceProvider();

        await using SangamDbContext db = _pg.CreateContext();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        SangamUser[] people = [TestUsers.New("owner@example.in", "+919000000201"), TestUsers.New("support@example.in", "+919000000202"), TestUsers.New("viewer@example.in", "+919000000203"), TestUsers.New("lost-phone@example.in", "+919000000204")];
        db.Users.AddRange(people);
        db.PlatformOperators.AddRange(
            new PlatformOperator { Id = Guid.NewGuid(), UserId = people[0].Id, Role = PlatformRole.Owner, GrantedAt = now },
            new PlatformOperator { Id = Guid.NewGuid(), UserId = people[1].Id, Role = PlatformRole.Support, GrantedAt = now },
            new PlatformOperator { Id = Guid.NewGuid(), UserId = people[2].Id, Role = PlatformRole.Viewer, GrantedAt = now });
        await db.SaveChangesAsync();
        (_owner, _support, _viewer, _person) = (people[0].Id, people[1].Id, people[2].Id, people[3].Id);
        foreach (Guid id in new[] { _owner, _support, _person })
        {
            await EnrolAsync(id);
        }
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
    public async Task ARequest_AlertsTheOwnerWithACancelLink_AndChangesNothingYet()
    {
        AdminResult result = await Admin().RequestTwoStepResetAsync(_support, _person, IdentityProofingMethod.VideoCall, "HELP-1042, video call", "198.51.100.4");
        Assert.True(result.Succeeded, result.Message);

        await using SangamDbContext db = _pg.CreateContext();
        Assert.True((await db.Users.SingleAsync(u => u.Id == _person)).TwoFactorEnabled);
        MfaResetRequest request = await db.MfaResetRequests.SingleAsync(r => r.UserId == _person);
        Assert.False(request.Privileged);
        Assert.InRange((request.EffectiveAt - request.RequestedAt).TotalHours, 23.99, 24.01);
        AuditEvent audit = await db.AuditEvents.SingleAsync(e => e.Action == AuditActions.AdminUserMfaResetRequest);
        Assert.Equal((_support, _person), (audit.ActorUserId!.Value, audit.TargetId!.Value));
        Assert.Contains("\"verified_by\":\"video_call\"", audit.Metadata.Replace(" ", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.False(await db.AuditEvents.AnyAsync(e => e.Action == AuditActions.AdminUserMfaReset));

        EmailMessageView mail = Latest("lost-phone@example.in");
        Assert.Equal("Sangam support was asked to reset your two-step sign-in", mail.Subject);
        Assert.Contains("/account/reset/cancel/", mail.Text, StringComparison.Ordinal);
        Assert.True(await Resets().NoticeDueAsync(_person));

        Assert.False((await Admin().RequestTwoStepResetAsync(_support, _person, IdentityProofingMethod.VideoCall, "HELP-1043", null)).Succeeded);
    }

    [PostgresFact]
    public async Task AfterTheCoolingOff_TheResetIsApplied_Once_AndThePersonIsTold()
    {
        string stampBefore;
        await using (SangamDbContext before = _pg.CreateContext())
        {
            stampBefore = (await before.Users.SingleAsync(u => u.Id == _person)).SecurityStamp!;
        }

        Assert.True((await Admin().RequestTwoStepResetAsync(_support, _person, IdentityProofingMethod.InPerson, "HELP-7", null)).Succeeded);
        Assert.Equal(0, await Resets().ApplyDueAsync());
        await DueNowAsync(_person);
        Assert.Equal(1, await Resets().ApplyDueAsync());
        Assert.Equal(0, await Resets().ApplyDueAsync());

        await using SangamDbContext db = _pg.CreateContext();
        SangamUser user = await db.Users.SingleAsync(u => u.Id == _person);
        Assert.False(user.TwoFactorEnabled);
        Assert.NotEqual(stampBefore, user.SecurityStamp);
        AuditEvent applied = await db.AuditEvents.SingleAsync(e => e.Action == AuditActions.AdminUserMfaReset);
        Assert.Contains("after_cooling_off", applied.Metadata, StringComparison.Ordinal);
        Assert.Equal("Your Sangam two-step sign-in was reset", Latest("lost-phone@example.in").Subject);
    }

    [PostgresFact]
    public async Task TheOwnerCancelsWithTheLink_AndTheResetNeverHappens()
    {
        Assert.True((await Admin().RequestTwoStepResetAsync(_support, _person, IdentityProofingMethod.VideoCall, "HELP-9", null)).Succeeded);
        string text = Latest("lost-phone@example.in").Text;
        string token = text[(text.IndexOf("/account/reset/cancel/", StringComparison.Ordinal) + "/account/reset/cancel/".Length)..].Split('\n')[0].Trim();

        Assert.NotNull(await Resets().FindByTokenAsync(token));
        Assert.True(await Resets().CancelByTokenAsync(token, "203.0.113.9"));
        Assert.False(await Resets().CancelByTokenAsync(token, null));
        Assert.Null(await Resets().FindByTokenAsync(token));

        await DueNowAsync(_person, includeCancelled: true);
        Assert.Equal(0, await Resets().ApplyDueAsync());
        await using SangamDbContext db = _pg.CreateContext();
        Assert.True((await db.Users.SingleAsync(u => u.Id == _person)).TwoFactorEnabled);
        Assert.True(await db.AuditEvents.AnyAsync(e => e.Action == AuditActions.UserMfaResetCancel && e.TargetId == _person));
    }

    [PostgresFact]
    public async Task PrivilegedAccounts_WaitSeventyTwoHours()
    {
        await using (SangamDbContext db = _pg.CreateContext())
        {
            App app = new() { Id = Guid.NewGuid(), ClientId = "his", Slug = "his", DisplayName = "LiPi HIS", OwnerCompanyName = "Partner Pvt Ltd", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
            db.Apps.Add(app);
            db.AppAdmins.Add(new AppAdmin { Id = Guid.NewGuid(), AppId = app.Id, UserId = _person, Role = AppAdminRole.Owner, GrantedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }

        Assert.True((await Admin().RequestTwoStepResetAsync(_support, _person, IdentityProofingMethod.InPerson, "HELP-11", null)).Succeeded);
        Assert.True((await Admin().RequestTwoStepResetAsync(_owner, _support, IdentityProofingMethod.InPerson, "Owner, in person", null)).Succeeded);
        await using SangamDbContext check = _pg.CreateContext();
        foreach (MfaResetRequest request in await check.MfaResetRequests.ToListAsync())
        {
            Assert.True(request.Privileged);
            Assert.InRange((request.EffectiveAt - request.RequestedAt).TotalHours, 71.99, 72.01);
        }
    }

    [PostgresFact]
    public async Task TheUrgentOverride_NeedsAReason_AppliesAtOnce_IsAuditedApart_AndAlertsTheOwner()
    {
        Assert.True((await Admin().RequestTwoStepResetAsync(_support, _person, IdentityProofingMethod.VideoCall, "HELP-12", null)).Succeeded);
        Assert.False((await Admin().ApplyTwoStepResetNowAsync(_support, _person, IdentityProofingMethod.VideoCall, "HELP-12", "now", null)).Succeeded);
        Assert.False((await Admin().ApplyTwoStepResetNowAsync(_viewer, _person, IdentityProofingMethod.VideoCall, "HELP-12", "Doctor locked out on a night shift", null)).Succeeded);

        AdminResult result = await Admin().ApplyTwoStepResetNowAsync(_support, _person, IdentityProofingMethod.VideoCall, "HELP-12", "Doctor locked out on a night shift", "198.51.100.4");
        Assert.True(result.Succeeded, result.Message);

        await using SangamDbContext db = _pg.CreateContext();
        Assert.False((await db.Users.SingleAsync(u => u.Id == _person)).TwoFactorEnabled);
        AuditEvent urgent = await db.AuditEvents.SingleAsync(e => e.Action == AuditActions.AdminUserMfaResetUrgent);
        Assert.Contains("night shift", urgent.Metadata, StringComparison.Ordinal);
        Assert.Contains("urgent_override", (await db.AuditEvents.SingleAsync(e => e.Action == AuditActions.AdminUserMfaReset)).Metadata, StringComparison.Ordinal);
        Assert.True(await db.AuditEvents.AnyAsync(e => e.Action == AuditActions.PlatformAlert));
        Assert.StartsWith("SangamID alert: urgent 2-step reset", Latest("owner@example.in").Subject, StringComparison.Ordinal);
    }

    [PostgresFact]
    public async Task ARequest_IsRefused_WithoutTheRank_ForYourself_ForAnOperatorBelowOwner_AndWithAnIdNumber()
    {
        IAdminService admin = Admin();
        Assert.False((await admin.RequestTwoStepResetAsync(_viewer, _person, IdentityProofingMethod.InPerson, "HELP-1", null)).Succeeded);
        Assert.False((await admin.RequestTwoStepResetAsync(_support, _support, IdentityProofingMethod.InPerson, "HELP-2", null)).Succeeded);
        Assert.False((await admin.RequestTwoStepResetAsync(_support, _owner, IdentityProofingMethod.InPerson, "HELP-3", null)).Succeeded);

        AdminResult idNumber = await admin.RequestTwoStepResetAsync(_support, _person, IdentityProofingMethod.InPerson, "Aadhaar 1234 5678 9012 seen, 123456789012", null);
        Assert.False(idNumber.Succeeded);
        Assert.Contains("identity-document number", idNumber.Message, StringComparison.Ordinal);

        await using SangamDbContext db = _pg.CreateContext();
        Assert.False(await db.MfaResetRequests.AnyAsync());
        Assert.False(await db.AuditEvents.AnyAsync(e => e.Action == AuditActions.AdminUserMfaResetRequest));
    }

    private async Task DueNowAsync(Guid userId, bool includeCancelled = false)
    {
        await using SangamDbContext db = _pg.CreateContext();
        await db.MfaResetRequests.Where(r => r.UserId == userId && (includeCancelled || r.CancelledAt == null))
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.EffectiveAt, DateTimeOffset.UtcNow.AddMinutes(-1)));
    }

    private EmailMessageView Latest(string email)
    {
        Application.Abstractions.EmailMessage message = _provider.GetRequiredService<InMemoryEmailOutbox>().LatestFor(email)!.Message;
        return new EmailMessageView(message.Subject, message.TextBody);
    }

    private IMfaResetService Resets()
    {
        IServiceScope scope = _provider.CreateScope();
        _scopes.Add(scope);
        return scope.ServiceProvider.GetRequiredService<IMfaResetService>();
    }

    private sealed record EmailMessageView(string Subject, string Text);

    private IAdminService Admin()
    {
        IServiceScope scope = _provider.CreateScope();
        _scopes.Add(scope);
        return scope.ServiceProvider.GetRequiredService<IAdminService>();
    }

    private async Task EnrolAsync(Guid userId)
    {
        using IServiceScope scope = _provider.CreateScope();
        UserManager<SangamUser> users = scope.ServiceProvider.GetRequiredService<UserManager<SangamUser>>();
        SangamUser user = (await users.FindByIdAsync(userId.ToString("D")))!;
        await users.ResetAuthenticatorKeyAsync(user);
        await users.SetTwoFactorEnabledAsync(user, true);
    }
}
