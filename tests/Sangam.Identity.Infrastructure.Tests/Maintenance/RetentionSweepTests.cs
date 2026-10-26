using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Abstractions;
using OpenIddict.EntityFrameworkCore.Models;
using Sangam.Identity.Application.Tenancy;
using Sangam.Identity.Domain.Entities;
using Sangam.Identity.Domain.Enums;
using Sangam.Identity.Infrastructure.Maintenance;
using Sangam.Identity.Infrastructure.Persistence;
using Sangam.Identity.Infrastructure.Tests.Accounts;
using Sangam.Identity.Infrastructure.Tests.Postgres;

namespace Sangam.Identity.Infrastructure.Tests.Maintenance;

/// <summary>rc.6 (SGM-910 section 6): short-lived records are deleted once their retention has passed, and not before.</summary>
[Collection("postgres")]
public sealed class RetentionSweepTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 10, 11, 6, 0, 0, TimeSpan.Zero);
    private readonly PostgresFixture _pg;
    private ServiceProvider _provider = null!;
    private Guid _user;
    private Guid _app;
    private Guid _org;

    public RetentionSweepTests(PostgresFixture pg)
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
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Sangam"] = _pg.ConnectionString,
            ["Sangam:Email:UseOutbox"] = "true",
            ["Sangam:Maintenance:Enabled"] = "false",
            ["Sangam:DataProtection:PersistKeys"] = "false",
        }).Build();
        ServiceCollection services = new();
        services.AddLogging();
        services.AddDataProtection();
        services.AddSingleton(configuration);
        services.AddSangamInfrastructure(configuration);
        _provider = services.BuildServiceProvider();

        await using SangamDbContext db = _pg.CreateContext();
        SangamUser user = TestUsers.New("asha@retention.test", "+919876506001");
        db.Users.Add(user);
        App app = new() { Id = Guid.NewGuid(), ClientId = "lipi-his", Slug = "lipi-his", DisplayName = "LiPi HIS", OwnerCompanyName = "imagiQa", CreatedAt = Now, UpdatedAt = Now };
        db.Apps.Add(app);
        await db.SaveChangesAsync();
        (_user, _app, _org) = (user.Id, app.Id, Guid.NewGuid());
        using IServiceScope scope = _provider.CreateScope();
        IManagementService management = scope.ServiceProvider.GetRequiredService<IManagementService>();
        await management.UpsertRoleAsync(_app, "nurse", new RoleUpsert("Nurse", null, ["patient:read"], null), ManagementActor.Api);
        await management.UpsertOrganisationAsync(_app, _org, new OrganisationUpsert("Apulki", "hospital", null, null), ManagementActor.Api);
    }

    public async Task DisposeAsync()
    {
        if (_provider is not null)
        {
            await _provider.DisposeAsync();
        }
    }

    [PostgresFact]
    public async Task EachKindOfRecord_IsDeletedAfterItsRetention_AndKeptBefore()
    {
        await using (SangamDbContext db = _pg.CreateContext())
        {
            // One-time codes and passkey challenges: a day after they expire.
            db.OneTimeCodes.AddRange(Code("old", Now.AddDays(-2)), Code("fresh", Now.AddHours(-12)));
            db.PasskeyChallenges.AddRange(Challenge(Now.AddDays(-2)), Challenge(Now.AddHours(-1)));

            // Invitations: 30 days after they are accepted, revoked or expire; an open one stays.
            db.Invitations.AddRange(
                Invitation("accepted-old@x.test", Now.AddDays(-60), accepted: Now.AddDays(-31)),
                Invitation("revoked-old@x.test", Now.AddDays(-60), revoked: Now.AddDays(-31)),
                Invitation("expired-old@x.test", Now.AddDays(-31)),
                Invitation("accepted-new@x.test", Now.AddDays(-60), accepted: Now.AddDays(-29)),
                Invitation("open@x.test", Now.AddDays(5)));

            // E-mail change requests and two-step reset requests: 30 days after they finish.
            db.EmailChangeRequests.AddRange(
                new EmailChangeRequest { Id = Guid.NewGuid(), UserId = _user, NewEmail = "a@x.test", NormalizedNewEmail = "A@X.TEST", CreatedAt = Now.AddDays(-40), CompletedAt = Now.AddDays(-40) },
                new EmailChangeRequest { Id = Guid.NewGuid(), UserId = _user, NewEmail = "b@x.test", NormalizedNewEmail = "B@X.TEST", CreatedAt = Now.AddDays(-40) },
                new EmailChangeRequest { Id = Guid.NewGuid(), UserId = _user, NewEmail = "c@x.test", NormalizedNewEmail = "C@X.TEST", CreatedAt = Now.AddDays(-2), CancelledAt = Now.AddDays(-2) });
            db.MfaResetRequests.AddRange(
                Reset(applied: Now.AddDays(-31)),
                Reset(cancelled: Now.AddDays(-31)),
                Reset(applied: Now.AddDays(-5)),
                Reset());

            // Account merges (rc.7): a cancelled one 30 days after; an applied one is kept for "merged into".
            SangamUser other = TestUsers.New("asha.two@retention.test", "+919876506002");
            db.Users.Add(other);
            db.AccountMergeRequests.AddRange(
                Merge(other.Id, cancelled: Now.AddDays(-31)),
                Merge(other.Id, cancelled: Now.AddDays(-5)),
                Merge(other.Id, applied: Now.AddDays(-400)));

            // SMS log: 180 days.
            db.SmsMessages.AddRange(Sms(Now.AddDays(-181)), Sms(Now.AddDays(-179)));

            // Grievances: 3 years after closure, with their entries; open ones stay.
            db.Grievances.AddRange(Grievance("G-OLD", Now.AddYears(-3).AddDays(-1)), Grievance("G-RECENT", Now.AddYears(-3).AddDays(1)), Grievance("G-OPEN", null));
            await db.SaveChangesAsync();
        }

        await using (SangamDbContext db = _pg.CreateContext())
        {
            int removed = await RetentionSweep.RunAsync(db, Now, CancellationToken.None);
            Assert.Equal(13, removed);
        }

        await using (SangamDbContext db = _pg.CreateContext())
        {
            Assert.Equal(["fresh"], await db.OneTimeCodes.Select(c => c.CodeHash).ToListAsync());
            Assert.Equal(1, await db.PasskeyChallenges.CountAsync());
            Assert.Equal(["accepted-new@x.test", "open@x.test"], await db.Invitations.OrderBy(i => i.Email).Select(i => i.Email).ToListAsync());
            Assert.Equal(["c@x.test"], await db.EmailChangeRequests.Select(r => r.NewEmail).ToListAsync());
            Assert.Equal(2, await db.MfaResetRequests.CountAsync());
            Assert.Equal(2, await db.AccountMergeRequests.CountAsync());
            Assert.True(await db.AccountMergeRequests.AnyAsync(r => r.AppliedAt != null));
            Assert.Equal(1, await db.SmsMessages.CountAsync());
            Assert.Equal(["G-OPEN", "G-RECENT"], await db.Grievances.OrderBy(g => g.Reference).Select(g => g.Reference).ToListAsync());
            Assert.Equal(2, await db.GrievanceEntries.CountAsync());
        }
    }

    [PostgresFact]
    public async Task AChainOfMerges_IsForgotten_WhenTheLastSurvivorIsDeleted()
    {
        // rc.16: A was merged into B, and B later into C. Deleting C must not leave A answering "merged into" B, a closed
        // account. A merge elsewhere (D into E) is untouched.
        SangamUser a = TestUsers.New("a@chain.test", "+919876506011");
        SangamUser b = TestUsers.New("b@chain.test", "+919876506012");
        SangamUser c = TestUsers.New("c@chain.test", "+919876506013");
        SangamUser d = TestUsers.New("d@chain.test", "+919876506014");
        SangamUser e = TestUsers.New("e@chain.test", "+919876506015");
        await using (SangamDbContext db = _pg.CreateContext())
        {
            db.Users.AddRange(a, b, c, d, e);
            db.AccountMergeRequests.AddRange(
                Applied(b.Id, a.Id),
                Applied(c.Id, b.Id),
                Applied(e.Id, d.Id));
            await db.SaveChangesAsync();
        }

        await using (SangamDbContext db = _pg.CreateContext())
        {
            Assert.Equal(2, await AccountPurgeService.ForgetMergesAsync(db, [c.Id]));
        }

        await using SangamDbContext check = _pg.CreateContext();
        AccountMergeRequest left = await check.AccountMergeRequests.SingleAsync();
        Assert.Equal((e.Id, d.Id), (left.SurvivorUserId, left.ClosedUserId));
    }

    [PostgresFact]
    public async Task FinishedSignInTokens_AreDeletedAfterThirtyDays_AndUsableOnesAreKept()
    {
        // rc.16 (decision of 26 Oct 2026): OpenIddict's rows were never deleted. Now a token that can no longer be used,
        // and an authorization with nothing left attached, go once they are more than 30 days old.
        DateTime now = DateTime.UtcNow;
        await using (SangamDbContext db = _pg.CreateContext())
        {
            OpenIddictEntityFrameworkCoreAuthorization ended = Authorization(OpenIddictConstants.Statuses.Revoked, now.AddDays(-40));
            OpenIddictEntityFrameworkCoreAuthorization endedButRecentToken = Authorization(OpenIddictConstants.Statuses.Revoked, now.AddDays(-40));
            OpenIddictEntityFrameworkCoreAuthorization valid = Authorization(OpenIddictConstants.Statuses.Valid, now.AddDays(-40));
            db.AddRange(ended, endedButRecentToken, valid);
            db.AddRange(
                Token("revoked-old", OpenIddictConstants.Statuses.Revoked, now.AddDays(-31), now.AddDays(-17), valid),
                Token("redeemed-old", OpenIddictConstants.Statuses.Redeemed, now.AddDays(-31), now.AddDays(-17), valid),
                Token("expired-old", OpenIddictConstants.Statuses.Valid, now.AddDays(-31), now.AddDays(-17), valid),
                Token("ended-authorization-old", OpenIddictConstants.Statuses.Valid, now.AddDays(-31), now.AddDays(10), ended),
                Token("valid-old", OpenIddictConstants.Statuses.Valid, now.AddDays(-31), now.AddDays(10), valid),
                Token("revoked-recent", OpenIddictConstants.Statuses.Revoked, now.AddDays(-29), now.AddDays(-15), endedButRecentToken));
            await db.SaveChangesAsync();
        }

        using (IServiceScope scope = _provider.CreateScope())
        {
            (long tokens, long authorizations) = await AccountPurgeService.PruneTokensAsync(
                scope.ServiceProvider.GetRequiredService<IOpenIddictTokenManager>(),
                scope.ServiceProvider.GetRequiredService<IOpenIddictAuthorizationManager>(),
                DateTimeOffset.UtcNow);
            Assert.Equal((4L, 1L), (tokens, authorizations));
        }

        await using SangamDbContext check = _pg.CreateContext();
        Assert.Equal(
            ["revoked-recent", "valid-old"],
            await check.Set<OpenIddictEntityFrameworkCoreToken>().OrderBy(t => t.ReferenceId).Select(t => t.ReferenceId!).ToListAsync());
        Assert.Equal(2, await check.Set<OpenIddictEntityFrameworkCoreAuthorization>().CountAsync());
    }

    private static OpenIddictEntityFrameworkCoreAuthorization Authorization(string status, DateTime created) => new()
    {
        Status = status,
        Subject = Guid.NewGuid().ToString("D"),
        Type = OpenIddictConstants.AuthorizationTypes.Permanent,
        CreationDate = created,
    };

    private static OpenIddictEntityFrameworkCoreToken Token(string name, string status, DateTime created, DateTime expires, OpenIddictEntityFrameworkCoreAuthorization authorization) => new()
    {
        ReferenceId = name,
        Status = status,
        Type = OpenIddictConstants.TokenTypeHints.RefreshToken,
        Subject = authorization.Subject,
        CreationDate = created,
        ExpirationDate = expires,
        Authorization = authorization,
    };

    private static AccountMergeRequest Applied(Guid survivor, Guid closed) => new()
    {
        Id = Guid.NewGuid(),
        SurvivorUserId = survivor,
        ClosedUserId = closed,
        RequestedAt = Now.AddDays(-10),
        EffectiveAt = Now.AddDays(-9),
        CancelTokenHash = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"),
        AppliedAt = Now.AddDays(-9),
    };

    private AccountMergeRequest Merge(Guid other, DateTimeOffset? cancelled = null, DateTimeOffset? applied = null) => new()
    {
        Id = Guid.NewGuid(),
        SurvivorUserId = _user,
        ClosedUserId = other,
        RequestedAt = Now.AddDays(-500),
        EffectiveAt = Now.AddDays(-499),
        CancelTokenHash = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"),
        CancelledAt = cancelled,
        CancelledBy = cancelled is null ? null : "owner",
        AppliedAt = applied,
    };

    private OneTimeCode Code(string hash, DateTimeOffset expires) => new() { Id = Guid.NewGuid(), UserId = _user, Purpose = default, CodeHash = hash, CreatedAt = expires.AddMinutes(-10), ExpiresAt = expires };

    private static PasskeyChallenge Challenge(DateTimeOffset expires) => new() { Id = Guid.NewGuid(), Kind = "assertion", OptionsJson = "{}", CreatedAt = expires.AddMinutes(-5), ExpiresAt = expires };

    private Invitation Invitation(string email, DateTimeOffset expires, DateTimeOffset? accepted = null, DateTimeOffset? revoked = null) => new()
    {
        Id = Guid.NewGuid(),
        AppId = _app,
        OrgId = _org,
        RoleCode = "nurse",
        Email = email,
        NormalizedEmail = email.ToUpperInvariant(),
        TokenHash = Guid.NewGuid().ToString("N"),
        CreatedAt = expires.AddDays(-7),
        ExpiresAt = expires,
        AcceptedAt = accepted,
        AcceptedByUserId = accepted is null ? null : _user,
        RevokedAt = revoked,
    };

    private MfaResetRequest Reset(DateTimeOffset? applied = null, DateTimeOffset? cancelled = null) => new()
    {
        Id = Guid.NewGuid(),
        UserId = _user,
        RequestedByUserId = _user,
        VerificationMethod = "digilocker",
        Reference = "R",
        RequestedAt = Now.AddDays(-40),
        EffectiveAt = Now.AddDays(-39),
        CancelTokenHash = Guid.NewGuid().ToString("N"),
        AppliedAt = applied,
        CancelledAt = cancelled,
    };

    private static SmsMessage Sms(DateTimeOffset at) => new() { Id = Guid.NewGuid(), ToHash = "h", Template = "otp", Provider = "test", Status = SmsStatus.Delivered, CreatedAt = at };

    private Grievance Grievance(string reference, DateTimeOffset? closed) => new()
    {
        Id = Guid.NewGuid(),
        Reference = reference,
        ReceivedAt = Now.AddYears(-4),
        Channel = "email",
        Category = "access",
        ComplainantName = "Asha",
        ComplainantContact = "asha@retention.test",
        Summary = "Could not sign in",
        Status = closed is null ? GrievanceStatus.Acknowledged : GrievanceStatus.Resolved,
        AcknowledgeBy = Now.AddYears(-4),
        ResolveBy = Now.AddYears(-4),
        ClosedAt = closed,
        CreatedByUserId = _user,
        Entries = [new GrievanceEntry { At = Now.AddYears(-4), OperatorUserId = _user, Kind = "note", Text = "Logged." }],
    };
}
