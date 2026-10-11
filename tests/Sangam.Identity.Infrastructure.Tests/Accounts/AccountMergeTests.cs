using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Sangam.Identity.Application.Accounts;
using Sangam.Identity.Application.Tenancy;
using Sangam.Identity.Domain;
using Sangam.Identity.Domain.Entities;
using Sangam.Identity.Domain.Enums;
using Sangam.Identity.Infrastructure.Persistence;
using Sangam.Identity.Infrastructure.Services;
using Sangam.Identity.Infrastructure.Tests.Postgres;

namespace Sangam.Identity.Infrastructure.Tests.Accounts;

/// <summary>
/// rc.7 (SGM-914 section 9, the founder's rules of 11 October 2026): a person merges two of their accounts. The survivor's
/// own wins where both hold the same thing; applications are told and the closed id answers "merged into" to those that
/// knew it; the closed account's e-mail and mobile are released; the merge waits 24 or 72 hours with a cancel link to
/// both; operators, inactive accounts and two different DigiLocker identities are refused; an administrator's role moves
/// and the application's other owners are told.
/// </summary>
[Collection("postgres")]
public sealed partial class AccountMergeTests : IAsyncLifetime
{
    private readonly PostgresFixture _pg;
    private readonly List<IServiceScope> _scopes = [];
    private ServiceProvider _provider = null!;
    private Guid _old;
    private Guid _new;
    private Guid _operator;
    private Guid _coOwner;
    private Guid _stranger;
    private Guid _his;
    private Guid _lab;
    private Guid _other;
    private Guid _apulki;
    private Guid _baner;
    private Guid _ward;
    private Guid _badge;

    public AccountMergeTests(PostgresFixture pg)
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

        DateTimeOffset now = DateTimeOffset.UtcNow;
        await using (SangamDbContext db = _pg.CreateContext())
        {
            SangamUser[] people = [TestUsers.New("asha.old@example.in", "+919000000301"), TestUsers.New("asha.new@example.in", "+919000000302"), TestUsers.New("founder@example.in", "+919000000303"), TestUsers.New("coowner@example.in", "+919000000304"), TestUsers.New("stranger@example.in", "+919000000305")];
            db.Users.AddRange(people);
            (_old, _new, _operator, _coOwner, _stranger) = (people[0].Id, people[1].Id, people[2].Id, people[3].Id, people[4].Id);
            App his = new() { Id = Guid.NewGuid(), ClientId = "lipi-his", Slug = "lipi-his", DisplayName = "LiPi HIS", OwnerCompanyName = "imagiQa", CreatedAt = now, UpdatedAt = now };
            App lab = new() { Id = Guid.NewGuid(), ClientId = "lipi-lab", Slug = "lipi-lab", DisplayName = "LiPi Lab", OwnerCompanyName = "imagiQa", CreatedAt = now, UpdatedAt = now };
            App other = new() { Id = Guid.NewGuid(), ClientId = "other", Slug = "other", DisplayName = "Other", OwnerCompanyName = "Someone", CreatedAt = now, UpdatedAt = now };
            db.Apps.AddRange(his, lab, other);
            (_his, _lab, _other) = (his.Id, lab.Id, other.Id);
            db.PlatformOperators.Add(new PlatformOperator { Id = Guid.NewGuid(), UserId = _operator, Role = PlatformRole.Owner, GrantedAt = now });

            // The HIS listens for events (a webhook), so they are written.
            db.WebhookEndpoints.Add(new WebhookEndpoint { Id = Guid.NewGuid(), AppId = _his, Url = "https://his.example.in/hooks", Events = $"{AppEventTypes.AccountMerged},{AppEventTypes.UserUpdated}", ProtectedSecret = "x", CreatedAt = now, UpdatedAt = now });

            // The old account: HIS and Lab, a consent, administrator of Lab. The new one: HIS only. The co-owner owns Lab.
            db.AppGrants.AddRange(
                new AppGrant { Id = Guid.NewGuid(), UserId = _old, AppId = _his, GrantedAt = now.AddYears(-2), LastUsedAt = now.AddYears(-1) },
                new AppGrant { Id = Guid.NewGuid(), UserId = _old, AppId = _lab, GrantedAt = now.AddYears(-2) },
                new AppGrant { Id = Guid.NewGuid(), UserId = _new, AppId = _his, GrantedAt = now.AddDays(-10) },
                new AppGrant { Id = Guid.NewGuid(), UserId = _coOwner, AppId = _lab, GrantedAt = now });
            db.Consents.Add(new Consent { Id = Guid.NewGuid(), UserId = _old, AppId = _lab, Scope = "openid profile", ConsentVersion = "1", GrantedAt = now.AddYears(-2) });
            db.AppAdmins.AddRange(
                new AppAdmin { Id = Guid.NewGuid(), AppId = _lab, UserId = _old, Role = AppAdminRole.Admin, GrantedAt = now.AddYears(-1) },
                new AppAdmin { Id = Guid.NewGuid(), AppId = _lab, UserId = _coOwner, Role = AppAdminRole.Owner, GrantedAt = now.AddYears(-1) });

            // Values the HIS keeps: the old account has a ward and a badge, the new one its own ward.
            UserAttributeDefinition ward = new() { Id = Guid.NewGuid(), AppId = _his, Key = "ward", Label = "Ward", CreatedAt = now };
            UserAttributeDefinition badge = new() { Id = Guid.NewGuid(), AppId = _his, Key = "badge", Label = "Badge", CreatedAt = now };
            db.UserAttributeDefinitions.AddRange(ward, badge);
            (_ward, _badge) = (ward.Id, badge.Id);
            db.UserAttributeValues.AddRange(
                new UserAttributeValue { DefinitionId = _ward, UserId = _old, Value = "3", UpdatedAt = now },
                new UserAttributeValue { DefinitionId = _badge, UserId = _old, Value = "A1", UpdatedAt = now },
                new UserAttributeValue { DefinitionId = _ward, UserId = _new, Value = "7", UpdatedAt = now });
            await db.SaveChangesAsync();
        }

        // Roles at two hospitals: the old account is a nurse at both; the new one a doctor at Apulki.
        IManagementService management = Scoped<IManagementService>();
        foreach (string code in new[] { "nurse", "doctor" })
        {
            Assert.Equal(ManagementStatus.Ok, (await management.UpsertRoleAsync(_his, code, new RoleUpsert(code, null, [], null), ManagementActor.Api)).Status);
        }

        (_apulki, _baner) = (Guid.NewGuid(), Guid.NewGuid());
        Assert.Equal(ManagementStatus.Ok, (await management.UpsertOrganisationAsync(_his, _apulki, new OrganisationUpsert("Apulki", "hospital", null, null), ManagementActor.Api)).Status);
        Assert.Equal(ManagementStatus.Ok, (await management.UpsertOrganisationAsync(_his, _baner, new OrganisationUpsert("Baner", "hospital", null, null), ManagementActor.Api)).Status);
        Assert.Equal(ManagementStatus.Ok, (await management.UpsertMembershipAsync(_his, _apulki, _old, new MembershipUpsert("nurse", false), ManagementActor.Person(_old))).Status);
        Assert.Equal(ManagementStatus.Ok, (await management.UpsertMembershipAsync(_his, _baner, _old, new MembershipUpsert("nurse", false), ManagementActor.Person(_old))).Status);
        Assert.Equal(ManagementStatus.Ok, (await management.UpsertMembershipAsync(_his, _apulki, _new, new MembershipUpsert("doctor", false), ManagementActor.Person(_new))).Status);
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
    public async Task ThePreview_ShowsBothAccounts_AndWhatTheClosedOneWouldLose()
    {
        MergePreview preview = await Merges().PreviewAsync(_old, _new);
        Assert.Null(preview.Refusal);
        Assert.Equal("asha.old@example.in", preview.First.Email);
        Assert.Equal(["LiPi HIS", "LiPi Lab"], preview.First.Applications);
        Assert.Equal(["LiPi Lab"], preview.First.AdminOf);

        // The old account administers Lab, so the wait is the privileged one.
        Assert.True(preview.Privileged);
        Assert.Equal(72, preview.WaitHours);

        // If the new account stays, the old one loses its role at Apulki and its ward: the new one has its own.
        MergeLoss lost = Assert.Single(preview.LostIfSecondSurvives);
        Assert.Equal(("LiPi HIS", 1, 1), (lost.Application, lost.Memberships, lost.Attributes));
        Assert.Equal(("LiPi HIS", 1, 1), (preview.LostIfFirstSurvives.Single().Application, preview.LostIfFirstSurvives.Single().Memberships, preview.LostIfFirstSurvives.Single().Attributes));
    }

    [PostgresFact]
    public async Task Operators_InactiveAccounts_TheSameAccount_AndTwoDifferentPeople_AreRefused()
    {
        IAccountMergeService merges = Merges();
        Assert.Equal("same", (await merges.PreviewAsync(_new, _new)).Refusal);
        Assert.Equal("operator", await merges.CanStartAsync(_operator));
        Assert.Equal("operator", (await merges.RequestAsync(_new, _operator, null)).Refusal);

        await using (SangamDbContext db = _pg.CreateContext())
        {
            (await db.Users.SingleAsync(u => u.Id == _stranger)).Status = UserStatus.Suspended;
            DateTimeOffset now = DateTimeOffset.UtcNow;
            db.IdentityVerifications.AddRange(
                new IdentityVerification { Id = Guid.NewGuid(), UserId = _old, Method = "digilocker", SubjectHash = "hash-one", Name = "Asha Menon", DateOfBirth = new DateOnly(1990, 1, 1), Gender = Gender.Female, VerifiedAt = now },
                new IdentityVerification { Id = Guid.NewGuid(), UserId = _new, Method = "digilocker", SubjectHash = "hash-two", Name = "Asha K", DateOfBirth = new DateOnly(1990, 1, 1), Gender = Gender.Female, VerifiedAt = now });
            await db.SaveChangesAsync();
        }

        Assert.Equal("inactive", (await merges.RequestAsync(_new, _stranger, null)).Refusal);
        Assert.Equal("different_identity", (await merges.RequestAsync(_new, _old, null)).Refusal);

        await using SangamDbContext check = _pg.CreateContext();
        Assert.False(await check.AccountMergeRequests.AnyAsync());
    }

    [PostgresFact]
    public async Task ARequest_WaitsItsPeriod_TellsBothAccounts_AndEitherCanCancel()
    {
        MergeOutcome outcome = await Merges().RequestAsync(_new, _old, "198.51.100.7");
        Assert.True(outcome.Succeeded);

        await using SangamDbContext db = _pg.CreateContext();
        AccountMergeRequest request = await db.AccountMergeRequests.SingleAsync();
        Assert.Equal((_new, _old, true), (request.SurvivorUserId, request.ClosedUserId, request.Privileged));
        Assert.InRange((request.EffectiveAt - request.RequestedAt).TotalHours, 71.99, 72.01);
        Assert.Equal(2, await db.AuditEvents.CountAsync(e => e.Action == AuditActions.UserAccountMergeRequest));

        // Both accounts are told, each with the same link; the merge is pending from either side.
        (string Subject, string Text) toOld = Latest("asha.old@example.in");
        (string Subject, string Text) toNew = Latest("asha.new@example.in");
        Assert.Equal("Two Sangam accounts are about to be merged", toOld.Subject);
        Assert.Equal(toOld.Subject, toNew.Subject);
        Assert.Contains("a***@example.in will be closed into", toOld.Text.Replace("\n", " ", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Contains("72 hours", toNew.Text, StringComparison.Ordinal);
        string token = TokenRegex().Match(toOld.Text).Groups[1].Value;
        Assert.Equal(token, TokenRegex().Match(toNew.Text).Groups[1].Value);
        Assert.NotNull(await Merges().PendingAsync(_old));
        Assert.Equal("pending", (await Merges().RequestAsync(_new, _old, null)).Refusal);

        Assert.NotNull(await Merges().FindByTokenAsync(token));
        Assert.True(await Merges().CancelByTokenAsync(token, null));
        Assert.False(await Merges().CancelByTokenAsync(token, null));
        Assert.Null(await Merges().PendingAsync(_new));
        Assert.Equal(2, await db.AuditEvents.CountAsync(e => e.Action == AuditActions.UserAccountMergeCancel));

        // Nothing moved.
        Assert.Equal(0, await Merges().ApplyDueAsync());
        Assert.True(await db.AppGrants.AnyAsync(g => g.UserId == _old && g.RevokedAt == null));
    }

    [PostgresFact]
    public async Task WhenDue_EverythingMoves_TheSurvivorsOwnWins_AndTheOldAccountIsClosed()
    {
        Assert.True((await Merges().RequestAsync(_new, _old, null)).Succeeded);
        await DueNowAsync();
        Assert.Equal(1, await Merges().ApplyDueAsync());
        Assert.Equal(0, await Merges().ApplyDueAsync());

        await using SangamDbContext db = _pg.CreateContext();

        // Applications and consent: the new account now has Lab too; the old account's connections are revoked, not deleted.
        HashSet<Guid> survivorApps = [.. await db.AppGrants.Where(g => g.UserId == _new && g.RevokedAt == null).Select(g => g.AppId).ToListAsync()];
        Assert.True(survivorApps.SetEquals([_his, _lab]));
        Assert.False(await db.AppGrants.AnyAsync(g => g.UserId == _old && g.RevokedAt == null));
        Assert.Equal(2, await db.AppGrants.CountAsync(g => g.UserId == _old));
        Assert.True(await db.Consents.AnyAsync(c => c.UserId == _new && c.AppId == _lab && c.RevokedAt == null));

        // Roles: the new account keeps its own doctor role at Apulki, and gains the nurse role at Baner.
        var roles = await db.OrgMemberships.Where(m => m.UserId == _new && m.RevokedAt == null).Select(m => new { m.OrgId, m.Role!.Code }).ToListAsync();
        Assert.Equal(2, roles.Count);
        Assert.Contains(roles, r => r.OrgId == _apulki && r.Code == "doctor");
        Assert.Contains(roles, r => r.OrgId == _baner && r.Code == "nurse");
        Assert.False(await db.OrgMemberships.AnyAsync(m => m.UserId == _old && m.RevokedAt == null));

        // Values: the new account's own ward stays; the badge moves; the old account has none left.
        Dictionary<Guid, string> values = await db.UserAttributeValues.Where(v => v.UserId == _new).ToDictionaryAsync(v => v.DefinitionId, v => v.Value);
        Assert.Equal("7", values[_ward]);
        Assert.Equal("A1", values[_badge]);
        Assert.False(await db.UserAttributeValues.AnyAsync(v => v.UserId == _old));

        // The administrator role moved; Lab's own audit records it, and its other owner is told.
        Assert.True(await db.AppAdmins.AnyAsync(a => a.UserId == _new && a.AppId == _lab && a.RevokedAt == null && a.Role == AppAdminRole.Admin));
        Assert.False(await db.AppAdmins.AnyAsync(a => a.UserId == _old && a.RevokedAt == null));
        Assert.True(await db.AuditEvents.AnyAsync(e => e.Action == AuditActions.AppAdminMerge && e.ActorAppId == _lab));
        Assert.Equal("An administrator of LiPi Lab merged their Sangam accounts", Latest("coowner@example.in").Subject);

        // The old account is closed: its e-mail is free again and its personal data gone; the row stays for the links.
        SangamUser closed = await db.Users.SingleAsync(u => u.Id == _old);
        Assert.Equal(UserStatus.DeletedHard, closed.Status);
        Assert.StartsWith("deleted-", closed.Email, StringComparison.Ordinal);
        Assert.Null(closed.PhoneNumber);
        Assert.Equal("Your Sangam accounts are merged", Latest("asha.new@example.in").Subject);

        // The HIS was told, with the surviving id; and about the survivor, so SCIM brings it in step.
        AppEvent merged = await db.AppEvents.SingleAsync(e => e.Type == AppEventTypes.AccountMerged);
        Assert.Equal((_his, _old), (merged.AppId, merged.UserId));
        Assert.Contains(_new.ToString("D"), merged.Data, StringComparison.Ordinal);
        Assert.True(await db.AppEvents.AnyAsync(e => e.Type == AppEventTypes.UserUpdated && e.UserId == _new && e.AppId == _his));
        Assert.Equal(2, await db.AuditEvents.CountAsync(e => e.Action == AuditActions.UserAccountMerge));

        // The closed id answers "merged into" — only to applications that knew it.
        MergedInto? answer = await Merges().MergedIntoAsync(_his, _old);
        Assert.Equal(_new, answer?.UserId);
        Assert.Null(await Merges().MergedIntoAsync(_other, _old));
        Assert.Null(await Merges().MergedIntoAsync(_his, _stranger));
        Assert.Null(await Merges().MergedIntoAsync(_his, _new));
    }

    [PostgresFact]
    public async Task WhenTheSurvivorIsDeleted_TheMergeRecordGoesToo()
    {
        Assert.True((await Merges().RequestAsync(_new, _old, null)).Succeeded);
        await DueNowAsync();
        Assert.Equal(1, await Merges().ApplyDueAsync());
        Assert.Equal(_new, (await Merges().MergedIntoAsync(_his, _old))?.UserId);

        // rc.8 (SGM-910): the surviving account reaches its deletion date; the record pointing at it is deleted with it.
        await using (SangamDbContext db = _pg.CreateContext())
        {
            await db.Users.Where(u => u.Id == _new).ExecuteUpdateAsync(s => s.SetProperty(u => u.PurgeAfter, DateTimeOffset.UtcNow.AddMinutes(-1)));
        }

        Infrastructure.Maintenance.AccountPurgeService purge = _provider.GetServices<Microsoft.Extensions.Hosting.IHostedService>().OfType<Infrastructure.Maintenance.AccountPurgeService>().Single();
        await purge.RunOnceAsync();

        await using SangamDbContext check = _pg.CreateContext();
        Assert.False(await check.AccountMergeRequests.AnyAsync());
        Assert.Null(await Merges().MergedIntoAsync(_his, _old));
    }

    [PostgresFact]
    public async Task AVerifiedIdentity_MovesToTheSurvivor_AndAnOwnerRoleIsNeverLost()
    {
        await using (SangamDbContext db = _pg.CreateContext())
        {
            db.IdentityVerifications.Add(new IdentityVerification { Id = Guid.NewGuid(), UserId = _old, Method = "digilocker", SubjectHash = "hash-asha", Name = "Asha Ravi Menon", DateOfBirth = new DateOnly(1988, 4, 12), Gender = Gender.Female, VerifiedAt = DateTimeOffset.UtcNow });
            (await db.Users.SingleAsync(u => u.Id == _old)).IdentityVerifiedAt = DateTimeOffset.UtcNow;

            // The old account owns Lab outright; the new one is an ordinary administrator there.
            AppAdmin oldAdmin = await db.AppAdmins.SingleAsync(a => a.UserId == _old);
            oldAdmin.Role = AppAdminRole.Owner;
            db.AppAdmins.Add(new AppAdmin { Id = Guid.NewGuid(), AppId = _lab, UserId = _new, Role = AppAdminRole.Admin, GrantedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }

        MergePreview preview = await Merges().PreviewAsync(_old, _new);
        Assert.True(preview.First.Verified);
        Assert.False(preview.Second.Verified);

        Assert.True((await Merges().RequestAsync(_new, _old, null)).Succeeded);
        await DueNowAsync();
        Assert.Equal(1, await Merges().ApplyDueAsync());

        await using SangamDbContext check = _pg.CreateContext();
        SangamUser survivor = await check.Users.SingleAsync(u => u.Id == _new);
        Assert.NotNull(survivor.IdentityVerifiedAt);
        Assert.Equal(("Asha Ravi", "Menon", new DateOnly(1988, 4, 12)), (survivor.FirstName, survivor.LastName, survivor.DateOfBirth));
        Assert.Equal("hash-asha", (await check.IdentityVerifications.SingleAsync(v => v.UserId == _new)).SubjectHash);
        Assert.False(await check.IdentityVerifications.AnyAsync(v => v.UserId == _old));
        Assert.Equal(AppAdminRole.Owner, (await check.AppAdmins.SingleAsync(a => a.UserId == _new && a.RevokedAt == null)).Role);
    }

    [PostgresFact]
    public async Task ARuleThatFailsByTheTimeItIsDue_CancelsTheMerge()
    {
        Assert.True((await Merges().RequestAsync(_new, _old, null)).Succeeded);
        await using (SangamDbContext db = _pg.CreateContext())
        {
            (await db.Users.SingleAsync(u => u.Id == _new)).Status = UserStatus.Suspended;
            await db.SaveChangesAsync();
        }

        await DueNowAsync();
        Assert.Equal(0, await Merges().ApplyDueAsync());

        await using SangamDbContext check = _pg.CreateContext();
        AccountMergeRequest request = await check.AccountMergeRequests.SingleAsync();
        Assert.Equal("refused", request.CancelledBy);
        Assert.Null(request.AppliedAt);
        Assert.True(await check.AppGrants.AnyAsync(g => g.UserId == _old && g.RevokedAt == null));
        Assert.Contains(await check.AuditEvents.Where(e => e.Action == AuditActions.UserAccountMergeCancel).Select(e => e.Metadata).ToListAsync(), m => m.Contains("inactive", StringComparison.Ordinal));
    }

    private async Task DueNowAsync()
    {
        await using SangamDbContext db = _pg.CreateContext();
        await db.AccountMergeRequests.ExecuteUpdateAsync(s => s.SetProperty(r => r.EffectiveAt, DateTimeOffset.UtcNow.AddMinutes(-1)));
    }

    private (string Subject, string Text) Latest(string email)
    {
        Application.Abstractions.EmailMessage message = _provider.GetRequiredService<InMemoryEmailOutbox>().LatestFor(email)!.Message;
        return (message.Subject, message.TextBody);
    }

    private IAccountMergeService Merges() => Scoped<IAccountMergeService>();

    private T Scoped<T>()
        where T : notnull
    {
        IServiceScope scope = _provider.CreateScope();
        _scopes.Add(scope);
        return scope.ServiceProvider.GetRequiredService<T>();
    }

    [GeneratedRegex("/account/merge/cancel/([0-9a-f]{64})")]
    private static partial Regex TokenRegex();
}
