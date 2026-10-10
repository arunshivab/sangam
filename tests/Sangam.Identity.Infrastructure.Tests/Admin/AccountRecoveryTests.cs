using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Sangam.Identity.Application.Accounts;
using Sangam.Identity.Application.Admin;
using Sangam.Identity.Application.Verification;
using Sangam.Identity.Domain;
using Sangam.Identity.Domain.Entities;
using Sangam.Identity.Domain.Enums;
using Sangam.Identity.Infrastructure.Persistence;
using Sangam.Identity.Infrastructure.Services;
using Sangam.Identity.Infrastructure.Tests.Accounts;
using Sangam.Identity.Infrastructure.Tests.Postgres;

namespace Sangam.Identity.Infrastructure.Tests.Admin;

/// <summary>
/// rc.6 (SGM-914) over D-K: a person who lost their second step recovers their account with DigiLocker, with no support
/// staff. A record that matches starts the cooling-off period the owner can cancel; one that does not waits for an
/// operator's review; the founder's urgent override is audited apart and alerts.
/// </summary>
[Collection("postgres")]
public sealed class AccountRecoveryTests : IAsyncLifetime
{
    private static readonly VerifiedIdentity Asha = new("digilocker", "dl-asha", "Asha Ravi Menon", new DateOnly(1988, 4, 12), Gender.Female);
    private readonly PostgresFixture _pg;
    private readonly List<IServiceScope> _scopes = [];
    private ServiceProvider _provider = null!;
    private Guid _owner;
    private Guid _support;
    private Guid _viewer;
    private Guid _person;

    public AccountRecoveryTests(PostgresFixture pg)
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
            ["Sangam:DigiLocker:SubjectKey"] = "a-test-key-for-digilocker-ids-0123456789",
        }).Build();
        services.AddSingleton(configuration);
        services.AddSangamInfrastructure(configuration);
        _provider = services.BuildServiceProvider();

        await using SangamDbContext db = _pg.CreateContext();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        SangamUser[] people = [TestUsers.New("owner@example.in", "+919000000201"), TestUsers.New("support@example.in", "+919000000202"), TestUsers.New("viewer@example.in", "+919000000203"), TestUsers.New("lost-phone@example.in", "+919000000204")];
        (people[3].FirstName, people[3].LastName, people[3].DateOfBirth, people[3].Gender) = ("Asha", "Menon", new DateOnly(1988, 4, 12), Gender.Female);
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
    public async Task AMatchingRecord_StartsTheCoolingOff_AlertsTheOwner_AndKeepsOnlyTheHash()
    {
        // The profile has no middle name; DigiLocker's record has one. Every profile word is present, so it matches.
        RecoveryOutcome outcome = await Recovery().StartAsync(_person, Asha, "198.51.100.4");
        Assert.Equal("started", outcome.Code);

        await using SangamDbContext db = _pg.CreateContext();
        Assert.True((await db.Users.SingleAsync(u => u.Id == _person)).TwoFactorEnabled);
        MfaResetRequest request = await db.MfaResetRequests.SingleAsync(r => r.UserId == _person);
        Assert.Equal(("digilocker", "rule", _person), (request.VerificationMethod, request.Reference, request.RequestedByUserId));
        Assert.Null(request.ReviewStatus);
        Assert.InRange((request.EffectiveAt - request.RequestedAt).TotalHours, 23.99, 24.01);
        Assert.DoesNotContain("dl-asha", request.Record, StringComparison.Ordinal);
        Assert.Contains("subject_hash", request.Record, StringComparison.Ordinal);
        AuditEvent audit = await db.AuditEvents.SingleAsync(e => e.Action == AuditActions.UserMfaRecoveryRequest);
        Assert.Contains("\"match\":\"rule\"", audit.Metadata.Replace(" ", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);

        EmailMessageView mail = Latest("lost-phone@example.in");
        Assert.Equal("Someone asked to recover your Sangam account", mail.Subject);
        Assert.Contains("/account/reset/cancel/", mail.Text, StringComparison.Ordinal);
        Assert.True(await Resets().NoticeDueAsync(_person));
        Assert.Equal("pending", (await Recovery().StartAsync(_person, Asha, null)).Code);
    }

    [PostgresFact]
    public async Task AfterTheCoolingOff_TheResetIsApplied_Once_ThePersonIsTold_AndNowVerified()
    {
        string stampBefore;
        await using (SangamDbContext before = _pg.CreateContext())
        {
            stampBefore = (await before.Users.SingleAsync(u => u.Id == _person)).SecurityStamp!;
        }

        Assert.Equal("started", (await Recovery().StartAsync(_person, Asha, null)).Code);
        Assert.Equal(0, await Resets().ApplyDueAsync());
        await DueNowAsync(_person);
        Assert.Equal(1, await Resets().ApplyDueAsync());
        Assert.Equal(0, await Resets().ApplyDueAsync());

        await using SangamDbContext db = _pg.CreateContext();
        SangamUser user = await db.Users.SingleAsync(u => u.Id == _person);
        Assert.False(user.TwoFactorEnabled);
        Assert.NotEqual(stampBefore, user.SecurityStamp);
        Assert.NotNull(user.IdentityVerifiedAt);
        Assert.Equal(("Asha Ravi", "Menon"), (user.FirstName, user.LastName));
        Assert.Null((await db.MfaResetRequests.SingleAsync(r => r.UserId == _person)).Record);
        Assert.Contains("after_cooling_off", (await db.AuditEvents.SingleAsync(e => e.Action == AuditActions.AdminUserMfaReset)).Metadata, StringComparison.Ordinal);
        Assert.Equal("Your Sangam two-step sign-in was reset", Latest("lost-phone@example.in").Subject);

        // Verified now: a later recovery with the same DigiLocker identity is a strong match, with another a review.
        await EnrolAsync(_person);
        Assert.Equal("started", (await Recovery().StartAsync(_person, Asha with { Name = "Someone Else" }, null)).Code);
        Assert.Equal("strong", (await db.MfaResetRequests.AsNoTracking().Where(r => r.UserId == _person && r.AppliedAt == null).SingleAsync()).Reference);
    }

    [PostgresFact]
    public async Task AMismatch_WaitsForReview_NeverAppliesOnItsOwn_AndTheOperatorSeesOnlyNameDateAndGender()
    {
        RecoveryOutcome outcome = await Recovery().StartAsync(_person, Asha with { DateOfBirth = new DateOnly(1988, 12, 4) }, null);
        Assert.Equal("review", outcome.Code);
        Assert.Equal("Someone asked to recover your Sangam account", Latest("lost-phone@example.in").Subject);
        Assert.Contains("operator will compare", Latest("lost-phone@example.in").Text, StringComparison.Ordinal);
        Assert.True((await Resets().PendingAsync(_person))!.AwaitingReview);

        await DueNowAsync(_person);
        Assert.Equal(0, await Resets().ApplyDueAsync());

        Assert.Empty(await Recovery().WaitingAsync(_viewer));
        RecoveryReview review = Assert.Single(await Recovery().WaitingAsync(_support));
        Assert.Equal(("Asha Menon", "Asha Ravi Menon", new DateOnly(1988, 12, 4)), (review.ProfileName, review.RecordName, review.RecordDateOfBirth));
        Assert.Equal([RecoveryMatch.DateOfBirth], review.Mismatches);

        Assert.False((await Recovery().ApproveAsync(_support, review.RequestId, "ok", null)).Succeeded);
        Assert.False((await Recovery().ApproveAsync(_support, review.RequestId, "Seen Aadhaar 123456789012, same person", null)).Succeeded);
        AdminResult approved = await Recovery().ApproveAsync(_support, review.RequestId, "Day and month swapped on the profile; same person.", null);
        Assert.True(approved.Succeeded, approved.Message);

        await using SangamDbContext db = _pg.CreateContext();
        MfaResetRequest request = await db.MfaResetRequests.SingleAsync(r => r.UserId == _person);
        Assert.Equal((MfaResetReview.Approved, _support), (request.ReviewStatus, request.ReviewedByUserId!.Value));
        Assert.True(request.EffectiveAt > DateTimeOffset.UtcNow.AddHours(23));
        Assert.Equal("Someone asked to recover your Sangam account", Latest("lost-phone@example.in").Subject);
        Assert.True(await db.AuditEvents.AnyAsync(e => e.Action == AuditActions.AdminUserMfaRecoveryApprove && e.TargetId == _person));
        Assert.Empty(await Recovery().WaitingAsync(_support));
    }

    [PostgresFact]
    public async Task ARefusedReview_ClearsTheRecord_AndTellsThePersonTheyMayStartAgain()
    {
        Assert.Equal("review", (await Recovery().StartAsync(_person, Asha with { Name = "Bindu Thomas" }, null)).Code);
        RecoveryReview review = Assert.Single(await Recovery().WaitingAsync(_owner));
        Assert.True((await Recovery().RefuseAsync(_owner, review.RequestId, "A different person's record entirely.", null)).Succeeded);
        Assert.False((await Recovery().RefuseAsync(_owner, review.RequestId, "A different person's record entirely.", null)).Succeeded);

        await using SangamDbContext db = _pg.CreateContext();
        MfaResetRequest request = await db.MfaResetRequests.SingleAsync(r => r.UserId == _person);
        Assert.Equal((MfaResetReview.Refused, "review"), (request.ReviewStatus, request.CancelledBy));
        Assert.Null(request.Record);
        Assert.True((await db.Users.SingleAsync(u => u.Id == _person)).TwoFactorEnabled);
        Assert.Equal("Your Sangam account recovery was refused", Latest("lost-phone@example.in").Subject);
        Assert.Null(await Resets().PendingAsync(_person));
    }

    [PostgresFact]
    public async Task TheOwnerCancelsWithTheLink_AndTheResetNeverHappens()
    {
        Assert.Equal("started", (await Recovery().StartAsync(_person, Asha, null)).Code);
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
        Assert.Null((await db.MfaResetRequests.SingleAsync(r => r.UserId == _person)).Record);
        Assert.True(await db.AuditEvents.AnyAsync(e => e.Action == AuditActions.UserMfaResetCancel && e.TargetId == _person));
    }

    [PostgresFact]
    public async Task PrivilegedAccounts_WaitSeventyTwoHours_AndNobodyReviewsTheirOwn()
    {
        await using (SangamDbContext db = _pg.CreateContext())
        {
            App app = new() { Id = Guid.NewGuid(), ClientId = "his", Slug = "his", DisplayName = "LiPi HIS", OwnerCompanyName = "Partner Pvt Ltd", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
            db.Apps.Add(app);
            db.AppAdmins.Add(new AppAdmin { Id = Guid.NewGuid(), AppId = app.Id, UserId = _person, Role = AppAdminRole.Owner, GrantedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }

        Assert.Equal("started", (await Recovery().StartAsync(_person, Asha, null)).Code);
        await using (SangamDbContext check = _pg.CreateContext())
        {
            MfaResetRequest request = await check.MfaResetRequests.SingleAsync();
            Assert.True(request.Privileged);
            Assert.InRange((request.EffectiveAt - request.RequestedAt).TotalHours, 71.99, 72.01);
        }

        Assert.Equal("review", (await Recovery().StartAsync(_support, Asha, null)).Code);
        RecoveryReview review = Assert.Single(await Recovery().WaitingAsync(_owner));
        Assert.Contains("own account", (await Recovery().ApproveAsync(_support, review.RequestId, "It is me, really, approve it.", null)).Message, StringComparison.Ordinal);
        Assert.Contains("needs Support or Owner", (await Recovery().ApproveAsync(_viewer, review.RequestId, "Same person, I checked it.", null)).Message, StringComparison.Ordinal);
        Assert.True((await Recovery().ApproveAsync(_owner, review.RequestId, "Same person; profile shortened the name.", null)).Succeeded);
    }

    [PostgresFact]
    public async Task ADigiLockerIdentityOnAnotherAccount_OrAnAccountWithoutASecondStep_IsRefused()
    {
        using (IServiceScope scope = _provider.CreateScope())
        {
            IIdentityVerificationService verification = scope.ServiceProvider.GetRequiredService<IIdentityVerificationService>();
            Assert.True((await verification.ApplyAsync(_viewer, Asha, null)).Succeeded);
        }

        Assert.Equal("taken", (await Recovery().StartAsync(_person, Asha, null)).Code);
        Assert.Equal("none", (await Recovery().StartAsync(_viewer, Asha, null)).Code);
        await using SangamDbContext db = _pg.CreateContext();
        Assert.False(await db.MfaResetRequests.AnyAsync());
    }

    [PostgresFact]
    public async Task TheUrgentOverride_IsTheOwnersAlone_NeedsAReason_AppliesAtOnce_AndAlerts()
    {
        Assert.Equal("review", (await Recovery().StartAsync(_person, Asha with { Gender = Gender.Male }, null)).Code);
        Assert.False((await Admin().ApplyTwoStepResetNowAsync(_owner, _person, "now", null)).Succeeded);
        Assert.False((await Admin().ApplyTwoStepResetNowAsync(_support, _person, "Doctor locked out on a night shift", null)).Succeeded);
        Assert.False((await Admin().ApplyTwoStepResetNowAsync(_owner, _owner, "Doctor locked out on a night shift", null)).Succeeded);

        AdminResult result = await Admin().ApplyTwoStepResetNowAsync(_owner, _person, "Doctor locked out on a night shift", "198.51.100.4");
        Assert.True(result.Succeeded, result.Message);

        await using SangamDbContext db = _pg.CreateContext();
        Assert.False((await db.Users.SingleAsync(u => u.Id == _person)).TwoFactorEnabled);
        AuditEvent urgent = await db.AuditEvents.SingleAsync(e => e.Action == AuditActions.AdminUserMfaResetUrgent);
        Assert.Contains("night shift", urgent.Metadata, StringComparison.Ordinal);
        Assert.Contains("\"skipped_review\":true", urgent.Metadata.Replace(" ", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.True(await db.AuditEvents.AnyAsync(e => e.Action == AuditActions.PlatformAlert));
        Assert.StartsWith("SangamID alert: urgent 2-step reset", Latest("owner@example.in").Subject, StringComparison.Ordinal);
        Assert.False((await Admin().ApplyTwoStepResetNowAsync(_owner, _support, "No request waiting here at all", null)).Succeeded);
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

    private IMfaResetService Resets() => Scoped<IMfaResetService>();

    private IAccountRecoveryService Recovery() => Scoped<IAccountRecoveryService>();

    private IAdminService Admin() => Scoped<IAdminService>();

    private T Scoped<T>()
        where T : notnull
    {
        IServiceScope scope = _provider.CreateScope();
        _scopes.Add(scope);
        return scope.ServiceProvider.GetRequiredService<T>();
    }

    private async Task EnrolAsync(Guid userId)
    {
        using IServiceScope scope = _provider.CreateScope();
        UserManager<SangamUser> users = scope.ServiceProvider.GetRequiredService<UserManager<SangamUser>>();
        SangamUser user = (await users.FindByIdAsync(userId.ToString("D")))!;
        await users.ResetAuthenticatorKeyAsync(user);
        await users.SetTwoFactorEnabledAsync(user, true);
    }

    private sealed record EmailMessageView(string Subject, string Text);
}
