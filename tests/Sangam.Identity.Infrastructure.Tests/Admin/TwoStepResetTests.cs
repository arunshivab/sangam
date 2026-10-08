using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Sangam.Identity.Application.Admin;
using Sangam.Identity.Domain;
using Sangam.Identity.Domain.Entities;
using Sangam.Identity.Domain.Enums;
using Sangam.Identity.Infrastructure.Persistence;
using Sangam.Identity.Infrastructure.Services;
using Sangam.Identity.Infrastructure.Tests.Accounts;
using Sangam.Identity.Infrastructure.Tests.Postgres;

namespace Sangam.Identity.Infrastructure.Tests.Admin;

/// <summary>PR-16 (CAP-019): support resets a lost authenticator only after proving identity, and the person is told.</summary>
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
    public async Task Support_ResetsALostAuthenticator_EndsEverySession_AndTellsThePerson()
    {
        string stampBefore;
        await using (SangamDbContext before = _pg.CreateContext())
        {
            stampBefore = (await before.Users.SingleAsync(u => u.Id == _person)).SecurityStamp!;
        }

        AdminResult result = await Admin().ResetTwoStepAsync(_support, _person, IdentityProofingMethod.VideoCall, "HELP-1042, video call", "198.51.100.4");
        Assert.True(result.Succeeded, result.Message);

        await using SangamDbContext db = _pg.CreateContext();
        SangamUser user = await db.Users.SingleAsync(u => u.Id == _person);
        Assert.False(user.TwoFactorEnabled);
        Assert.NotEqual(stampBefore, user.SecurityStamp);
        AuditEvent audit = await db.AuditEvents.SingleAsync(e => e.Action == AuditActions.AdminUserMfaReset);
        Assert.Equal((_support, _person), (audit.ActorUserId!.Value, audit.TargetId!.Value));
        Assert.Contains("video_call", audit.Metadata, StringComparison.Ordinal);
        Assert.Contains("HELP-1042", audit.Metadata, StringComparison.Ordinal);

        InMemoryEmailOutbox outbox = _provider.GetRequiredService<InMemoryEmailOutbox>();
        Assert.Equal("Your Sangam two-step sign-in was reset", outbox.LatestFor("lost-phone@example.in")!.Message.Subject);
    }

    [PostgresFact]
    public async Task TheReset_IsRefused_WithoutTheRank_ForYourself_ForAnOperatorBelowOwner_AndWithAnIdNumber()
    {
        IAdminService admin = Admin();
        Assert.False((await admin.ResetTwoStepAsync(_viewer, _person, IdentityProofingMethod.InPerson, "HELP-1", null)).Succeeded);
        Assert.False((await admin.ResetTwoStepAsync(_support, _support, IdentityProofingMethod.InPerson, "HELP-2", null)).Succeeded);
        Assert.False((await admin.ResetTwoStepAsync(_support, _owner, IdentityProofingMethod.InPerson, "HELP-3", null)).Succeeded);

        AdminResult idNumber = await admin.ResetTwoStepAsync(_support, _person, IdentityProofingMethod.InPerson, "Aadhaar 1234 5678 9012 seen, 123456789012", null);
        Assert.False(idNumber.Succeeded);
        Assert.Contains("identity-document number", idNumber.Message, StringComparison.Ordinal);

        await using (SangamDbContext db = _pg.CreateContext())
        {
            Assert.True((await db.Users.SingleAsync(u => u.Id == _person)).TwoFactorEnabled);
            Assert.False(await db.AuditEvents.AnyAsync(e => e.Action == AuditActions.AdminUserMfaReset));
        }

        // An Owner may reset another operator's second factor.
        Assert.True((await admin.ResetTwoStepAsync(_owner, _support, IdentityProofingMethod.InPerson, "Owner reset, in person", null)).Succeeded);
        Assert.False((await admin.ResetTwoStepAsync(_owner, _support, IdentityProofingMethod.InPerson, "Again", null)).Succeeded);
    }

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
