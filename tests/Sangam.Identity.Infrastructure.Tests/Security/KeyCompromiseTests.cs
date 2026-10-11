using System.Text.Json;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.EntityFrameworkCore.Models;
using Sangam.Identity.Application.Verification;
using Sangam.Identity.Domain;
using Sangam.Identity.Domain.Entities;
using Sangam.Identity.Domain.Enums;
using Sangam.Identity.Infrastructure.Accounts;
using Sangam.Identity.Infrastructure.Persistence;
using Sangam.Identity.Infrastructure.Security;
using Sangam.Identity.Infrastructure.Tests.Accounts;
using Sangam.Identity.Infrastructure.Tests.Postgres;
using Sangam.Identity.Infrastructure.Verification;

namespace Sangam.Identity.Infrastructure.Tests.Security;

/// <summary>
/// rc.9 (ASVS V1.6.3; docs/security/key-compromise.md): the key-compromise commands revoke every token, replace the key ring
/// while keeping authenticators working, and ask verified people to verify again; replacing the DigiLocker subject key does
/// not let one identity verify a second account.
/// </summary>
[Collection("postgres")]
public sealed class KeyCompromiseTests : IAsyncLifetime
{
    private const string OldSubjectKey = "the-old-digilocker-subject-key-0123456789";
    private const string NewSubjectKey = "the-new-digilocker-subject-key-9876543210";
    private const string Incident = "INC-2026-10-11-01";

    private readonly PostgresFixture _pg;

    public KeyCompromiseTests(PostgresFixture pg)
    {
        _pg = pg;
    }

    public async Task InitializeAsync()
    {
        if (_pg.IsAvailable)
        {
            await _pg.ResetAsync();
        }
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [PostgresFact]
    public async Task RevokeTokens_RevokesEveryTokenAndAuthorization_AndIsAudited()
    {
        await using (SangamDbContext db = _pg.CreateContext())
        {
            OpenIddictEntityFrameworkCoreAuthorization authorization = new() { Status = "valid", Subject = Guid.NewGuid().ToString("D"), Type = "permanent", CreationDate = DateTime.UtcNow };
            db.Add(authorization);
            db.Add(new OpenIddictEntityFrameworkCoreToken { Status = "valid", Type = "refresh_token", Authorization = authorization, CreationDate = DateTime.UtcNow });
            db.Add(new OpenIddictEntityFrameworkCoreToken { Status = "redeemed", Type = "authorization_code", CreationDate = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }

        await using ServiceProvider provider = Provider(OldSubjectKey);
        using StringWriter output = new();
        Assert.Equal(0, await KeyCompromiseCommand.RunAsync(provider, ["revoke-tokens", "--incident", Incident], output));
        Assert.Contains("2 tokens and 1 authorizations revoked", output.ToString(), StringComparison.Ordinal);

        await using SangamDbContext check = _pg.CreateContext();
        Assert.All(await check.Set<OpenIddictEntityFrameworkCoreToken>().ToListAsync(), t => Assert.Equal("revoked", t.Status));
        Assert.All(await check.Set<OpenIddictEntityFrameworkCoreAuthorization>().ToListAsync(), a => Assert.Equal("revoked", a.Status));
        AuditEvent audit = await check.AuditEvents.SingleAsync(e => e.Action == AuditActions.SecurityTokensRevokeAll);
        using JsonDocument meta = JsonDocument.Parse(audit.Metadata);
        Assert.Equal(Incident, meta.RootElement.GetProperty("incident").GetString());
        Assert.Equal(2, meta.RootElement.GetProperty("tokens").GetInt32());
    }

    [PostgresFact]
    public async Task RevokeKeyRing_KeepsAuthenticatorsWorking_UnderANewKey_AndSignsEveryoneOut()
    {
        await using ServiceProvider provider = Provider(OldSubjectKey);
        Guid userId;
        string secret;
        string storedBefore;
        string stampBefore;
        using (IServiceScope scope = provider.CreateScope())
        {
            SangamDbContext db = scope.ServiceProvider.GetRequiredService<SangamDbContext>();
            SangamUser user = TestUsers.New("keyring@compromise.test", "+919876506001");
            db.Users.Add(user);
            db.UserSessions.Add(new UserSession { Id = Guid.NewGuid(), UserId = user.Id, SignInMode = SignInMode.Password, CreatedAt = DateTimeOffset.UtcNow, LastSeenAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
            userId = user.Id;

            UserManager<SangamUser> users = scope.ServiceProvider.GetRequiredService<UserManager<SangamUser>>();
            SangamUser tracked = (await users.FindByIdAsync(userId.ToString("D")))!;
            await users.ResetAuthenticatorKeyAsync(tracked);
            secret = (await users.GetAuthenticatorKeyAsync(tracked))!;
            storedBefore = await StoredSecretAsync(userId);
            stampBefore = (await users.GetSecurityStampAsync(tracked))!;
        }

        IKey oldKey = Assert.Single(provider.GetRequiredService<IKeyManager>().GetAllKeys());

        using StringWriter output = new();
        Assert.Equal(0, await KeyCompromiseCommand.RunAsync(provider, ["revoke-key-ring", "--incident", Incident], output));
        Assert.Contains("1 authenticator secrets protected under the new key; 1 sessions ended", output.ToString(), StringComparison.Ordinal);

        // The old key is revoked and a new one made; the secret is protected again and still the same secret.
        IReadOnlyCollection<IKey> keys = provider.GetRequiredService<IKeyManager>().GetAllKeys();
        Assert.True(keys.Single(k => k.KeyId == oldKey.KeyId).IsRevoked);
        Assert.Contains(keys, k => !k.IsRevoked);
        Assert.NotEqual(storedBefore, await StoredSecretAsync(userId));
        using (IServiceScope scope = provider.CreateScope())
        {
            UserManager<SangamUser> users = scope.ServiceProvider.GetRequiredService<UserManager<SangamUser>>();
            SangamUser tracked = (await users.FindByIdAsync(userId.ToString("D")))!;
            Assert.Equal(secret, await users.GetAuthenticatorKeyAsync(tracked));
            Assert.NotEqual(stampBefore, await users.GetSecurityStampAsync(tracked));
        }

        await using SangamDbContext check = _pg.CreateContext();
        Assert.All(await check.UserSessions.Where(s => s.UserId == userId).ToListAsync(), s => Assert.Equal("key_compromise", s.RevokedReason));
        Assert.Single(await check.AuditEvents.Where(e => e.Action == AuditActions.SecurityKeyRingRevoke).ToListAsync());

        // A second run is safe: the secret, already under the new key, is left alone.
        using StringWriter again = new();
        Assert.Equal(0, await KeyCompromiseCommand.RunAsync(provider, ["revoke-key-ring", "--incident", Incident], again));
        using IServiceScope last = provider.CreateScope();
        UserManager<SangamUser> lastUsers = last.ServiceProvider.GetRequiredService<UserManager<SangamUser>>();
        Assert.Equal(secret, await lastUsers.GetAuthenticatorKeyAsync((await lastUsers.FindByIdAsync(userId.ToString("D")))!));
    }

    [PostgresFact]
    public async Task Reverify_AsksVerifiedPeople_AndANewSubjectKeyStillKeepsOneIdentityToOneAccount()
    {
        Guid ravi;
        Guid meera;
        await using (SangamDbContext db = _pg.CreateContext())
        {
            SangamUser r = TestUsers.New("ravi@compromise.test", "+919876506002");
            SangamUser m = TestUsers.New("meera@compromise.test", "+919876506003");
            db.Users.AddRange(r, m);
            await db.SaveChangesAsync();
            (ravi, meera) = (r.Id, m.Id);
        }

        VerifiedIdentity identity = new("digilocker", "DL-SUBJECT-0001", "Ravi Kumar", new DateOnly(1985, 3, 1), Gender.Male);
        await using (ServiceProvider before = Provider(OldSubjectKey))
        {
            using IServiceScope scope = before.CreateScope();
            Assert.True((await scope.ServiceProvider.GetRequiredService<IIdentityVerificationService>().ApplyAsync(ravi, identity, null)).Succeeded);
        }

        // The key is replaced, the old one kept as a previous key, and verified people are asked to verify again.
        await using ServiceProvider after = Provider(NewSubjectKey, OldSubjectKey);
        using StringWriter output = new();
        Assert.Equal(0, await KeyCompromiseCommand.RunAsync(after, ["reverify", "--incident", Incident], output));
        Assert.Contains("1 people asked", output.ToString(), StringComparison.Ordinal);
        await using (SangamDbContext db = _pg.CreateContext())
        {
            SangamUser r = await db.Users.SingleAsync(u => u.Id == ravi);
            Assert.NotNull(r.IdentityReverifyRequestedAt);
            Assert.NotNull(r.IdentityVerifiedAt);
        }

        using (IServiceScope scope = after.CreateScope())
        {
            IIdentityVerificationService verification = scope.ServiceProvider.GetRequiredService<IIdentityVerificationService>();

            // Someone else cannot use the same identity, though its stored hash was made with the old key.
            VerificationResult taken = await verification.ApplyAsync(meera, identity, null);
            Assert.False(taken.Succeeded);
            Assert.Equal("taken", taken.Code);

            // Ravi verifies again: the hash moves to the new key, and the request is cleared.
            Assert.True((await verification.ApplyAsync(ravi, identity, null)).Succeeded);
            string newHash = scope.ServiceProvider.GetRequiredService<EfIdentityVerificationService>().SubjectHash("digilocker", "DL-SUBJECT-0001");
            await using SangamDbContext db = _pg.CreateContext();
            Assert.Equal(newHash, (await db.IdentityVerifications.SingleAsync(v => v.UserId == ravi)).SubjectHash);
            Assert.Null((await db.Users.SingleAsync(u => u.Id == ravi)).IdentityReverifyRequestedAt);
        }
    }

    [Fact]
    public async Task WithoutAnIncident_NothingRuns()
    {
        ServiceCollection services = new();
        await using ServiceProvider empty = services.BuildServiceProvider();
        using StringWriter output = new();
        Assert.Equal(2, await KeyCompromiseCommand.RunAsync(empty, ["revoke-tokens"], output));
        Assert.Contains("Usage:", output.ToString(), StringComparison.Ordinal);
    }

    private async Task<string> StoredSecretAsync(Guid userId)
    {
        await using SangamDbContext db = _pg.CreateContext();
        return (await db.UserTokens.SingleAsync(t => t.UserId == userId && t.Name == SangamUserStore.AuthenticatorKeyName)).Value!;
    }

    private ServiceProvider Provider(string subjectKey, string? previousSubjectKey = null)
    {
        Dictionary<string, string?> settings = new()
        {
            ["ConnectionStrings:Sangam"] = _pg.ConnectionString,
            ["Sangam:Email:UseOutbox"] = "true",
            ["Sangam:Maintenance:Enabled"] = "false",
            ["Sangam:DigiLocker:SubjectKey"] = subjectKey,
        };
        if (previousSubjectKey is not null)
        {
            settings["Sangam:DigiLocker:PreviousSubjectKeys:0"] = previousSubjectKey;
        }

        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        ServiceCollection services = new();
        services.AddLogging();
        services.AddSingleton(configuration);
        services.AddSangamInfrastructure(configuration);
        return services.BuildServiceProvider();
    }
}
