using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Sangam.Identity.Application.Passkeys;
using Sangam.Identity.Domain;
using Sangam.Identity.Domain.Entities;
using Sangam.Identity.Domain.Enums;
using Sangam.Identity.Infrastructure.Persistence;
using Sangam.Identity.Infrastructure.Tests.Accounts;
using Sangam.Identity.Infrastructure.Tests.Postgres;

namespace Sangam.Identity.Infrastructure.Tests.Passkeys;

/// <summary>PR-14 and D-G: passkeys — real WebAuthn messages through ASP.NET Core Identity's verifier, and every refusal that matters.</summary>
[Collection("postgres")]
public sealed class PasskeyServiceTests : IAsyncLifetime
{
    private const string Origin = "https://id.sangam.test";
    private const string RpId = "id.sangam.test";
    private readonly PostgresFixture _pg;
    private readonly List<IServiceScope> _scopes = [];
    private ServiceProvider _provider = null!;
    private Guid _userId;

    public PasskeyServiceTests(PostgresFixture pg)
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
            ["Sangam:Issuer"] = Origin,
            ["Sangam:Maintenance:Enabled"] = "false",
            ["Sangam:DataProtection:PersistKeys"] = "false",
        }).Build();
        services.AddSingleton(configuration);
        services.AddSangamInfrastructure(configuration);
        _provider = services.BuildServiceProvider();
        await using SangamDbContext db = _pg.CreateContext();
        SangamUser user = TestUsers.New("kaveri@example.in", "+919000000031");
        db.Users.Add(user);
        await db.SaveChangesAsync();
        _userId = user.Id;
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
    public async Task APasskey_IsAdded_ThenSignsTheOwnerIn_AndTheCounterMovesForward()
    {
        using SoftwareAuthenticator phone = new();
        IPasskeyService passkeys = Service();

        PasskeyResult added = await AddAsync(passkeys, phone, "My phone");
        Assert.True(added.Succeeded, added.Message);
        Assert.Equal("My phone", Assert.Single(await passkeys.ListAsync(_userId)).Name);

        phone.Counter = 7;
        PasskeyResult signedIn = await SignInAsync(Service(), phone);

        Assert.True(signedIn.Succeeded, signedIn.Message);
        Assert.Equal(_userId, signedIn.UserId);
        await using SangamDbContext db = _pg.CreateContext();
        PasskeyCredential stored = await db.PasskeyCredentials.AsNoTracking().SingleAsync();
        Assert.NotNull(stored.LastUsedAt);
        IdentityUserPasskey<Guid> key = await db.UserPasskeys.AsNoTracking().SingleAsync();
        Assert.Equal(7u, key.Data.SignCount);
        Assert.Equal("My phone", key.Data.Name);
        Assert.Contains(AuditActions.UserPasskeyAdd, await db.AuditEvents.Select(e => e.Action).ToListAsync());
    }

    [PostgresFact]
    public async Task AChallenge_WorksOnce_SoACapturedAnswerCannotBeReplayed()
    {
        using SoftwareAuthenticator phone = new();
        await AddAsync(Service(), phone, "My phone");
        IPasskeyService passkeys = Service();
        PasskeyCeremony ceremony = await passkeys.BeginSignInAsync();
        phone.Counter = 1;
        string answer = phone.Get(ceremony.OptionsJson, Origin, RpId, _userId);

        Assert.True((await passkeys.CompleteSignInAsync(ceremony.ChallengeId, answer)).Succeeded);
        Assert.False((await Service().CompleteSignInAsync(ceremony.ChallengeId, answer)).Succeeded);
    }

    [PostgresFact]
    public async Task ACounterThatGoesBackwards_IsRefused_AsAPossibleClone()
    {
        using SoftwareAuthenticator phone = new();
        await AddAsync(Service(), phone, "My phone");
        phone.Counter = 10;
        Assert.True((await SignInAsync(Service(), phone)).Succeeded);

        phone.Counter = 4;
        Assert.False((await SignInAsync(Service(), phone)).Succeeded);

        await using SangamDbContext db = _pg.CreateContext();
        string? reason = await db.AuditEvents.Where(e => e.Action == AuditActions.UserPasskeyFail).Select(e => e.Metadata).SingleAsync();
        Assert.Contains("counter_regression", reason, StringComparison.Ordinal);
    }

    [PostgresFact]
    public async Task RegistrationWithoutUserVerification_IsRefused()
    {
        using SoftwareAuthenticator key = new();
        IPasskeyService passkeys = Service();
        PasskeyCeremony ceremony = await passkeys.BeginRegistrationAsync(_userId);

        PasskeyResult result = await passkeys.CompleteRegistrationAsync(_userId, ceremony.ChallengeId, key.Create(ceremony.OptionsJson, Origin, RpId, userVerified: false), "Key");

        Assert.False(result.Succeeded);
        Assert.Empty(await passkeys.ListAsync(_userId));
    }

    [PostgresFact]
    public async Task AnAnswerFromAnotherSite_IsRefused()
    {
        using SoftwareAuthenticator phone = new();
        await AddAsync(Service(), phone, "My phone");
        IPasskeyService passkeys = Service();
        PasskeyCeremony ceremony = await passkeys.BeginSignInAsync();
        phone.Counter = 1;

        // A look-alike site relays Sangam's challenge, but the browser stamps its own origin and rp id.
        string phished = phone.Get(ceremony.OptionsJson, "https://id.sangam-login.test", "id.sangam-login.test", _userId);

        Assert.False((await passkeys.CompleteSignInAsync(ceremony.ChallengeId, phished)).Succeeded);
    }

    [PostgresFact]
    public async Task ASuspendedAccount_OrARemovedPasskey_CannotSignIn()
    {
        using SoftwareAuthenticator phone = new();
        await AddAsync(Service(), phone, "My phone");
        await using (SangamDbContext db = _pg.CreateContext())
        {
            await db.Users.Where(u => u.Id == _userId).ExecuteUpdateAsync(s => s.SetProperty(u => u.Status, UserStatus.Suspended));
        }

        phone.Counter = 1;
        Assert.False((await SignInAsync(Service(), phone)).Succeeded);

        await using (SangamDbContext db = _pg.CreateContext())
        {
            await db.Users.Where(u => u.Id == _userId).ExecuteUpdateAsync(s => s.SetProperty(u => u.Status, UserStatus.Active));
        }

        IPasskeyService passkeys = Service();
        Assert.True(await passkeys.RemoveAsync(_userId, (await passkeys.ListAsync(_userId))[0].Id));

        // R7 (ASVS V2.5.5): the person is told.
        Assert.Equal("A passkey was removed from your Sangam account", _provider.GetRequiredService<Infrastructure.Services.InMemoryEmailOutbox>().LatestFor("kaveri@example.in")!.Message.Subject);
        phone.Counter = 2;
        Assert.False((await SignInAsync(Service(), phone)).Succeeded);
    }

    private IPasskeyService Service()
    {
        IServiceScope scope = _provider.CreateScope();
        _scopes.Add(scope);
        return scope.ServiceProvider.GetRequiredService<IPasskeyService>();
    }

    private async Task<PasskeyResult> AddAsync(IPasskeyService passkeys, SoftwareAuthenticator authenticator, string name)
    {
        PasskeyCeremony ceremony = await passkeys.BeginRegistrationAsync(_userId);
        return await passkeys.CompleteRegistrationAsync(_userId, ceremony.ChallengeId, authenticator.Create(ceremony.OptionsJson, Origin, RpId), name);
    }

    private async Task<PasskeyResult> SignInAsync(IPasskeyService passkeys, SoftwareAuthenticator authenticator)
    {
        PasskeyCeremony ceremony = await passkeys.BeginSignInAsync();
        return await passkeys.CompleteSignInAsync(ceremony.ChallengeId, authenticator.Get(ceremony.OptionsJson, Origin, RpId, _userId));
    }
}
