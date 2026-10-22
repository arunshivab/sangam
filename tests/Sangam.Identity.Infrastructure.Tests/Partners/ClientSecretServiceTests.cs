using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Abstractions;
using Sangam.Identity.Application.Accounts;
using Sangam.Identity.Application.Partners;
using Sangam.Identity.Domain;
using Sangam.Identity.Domain.Entities;
using Sangam.Identity.Domain.Enums;
using Sangam.Identity.Infrastructure.Persistence;
using Sangam.Identity.Infrastructure.Seeding;
using Sangam.Identity.Infrastructure.Services;
using Sangam.Identity.Infrastructure.Tests.Postgres;

namespace Sangam.Identity.Infrastructure.Tests.Partners;

/// <summary>
/// rc.11 (R5): two live client secrets, so an application's owner can rotate without a break in sign-in. Proved
/// against PostgreSQL and OpenIddict's own application manager, which is what the token endpoint asks.
/// </summary>
[Collection("postgres")]
public sealed class ClientSecretServiceTests : IAsyncLifetime
{
    private const string ClientId = "lipi-his";
    private const string IssuedSecret = "issued-secret-0123456789abcdefghijklmnop";
    private readonly List<IServiceScope> _scopes = [];
    private readonly PostgresFixture _pg;
    private ServiceProvider _provider = null!;
    private Guid _app;
    private Guid _owner;
    private Guid _admin;

    public ClientSecretServiceTests(PostgresFixture pg)
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
            ["Sangam:PasswordHashing:MemoryKiB"] = "8192",
            ["Sangam:PasswordHashing:Iterations"] = "2",
            ["Sangam:Otp:ResendCooldown"] = "00:00:00",
            ["Sangam:Maintenance:Enabled"] = "false",
            ["Sangam:DataProtection:PersistKeys"] = "false",
        }).Build();
        ServiceCollection services = new();
        services.AddLogging();
        services.AddDataProtection();
        services.AddSingleton(configuration);
        services.AddSangamInfrastructure(configuration);
        _provider = services.BuildServiceProvider();

        await RegisterAsync(IssuedSecret);
        _owner = await NewUserAsync("owner@lipi.test", "+919876502001");
        _admin = await NewUserAsync("admin@lipi.test", "+919876502002");

        await using SangamDbContext db = _pg.CreateContext();
        App app = await db.Apps.FirstAsync(a => a.ClientId == ClientId);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        db.AppAdmins.AddRange(
            new AppAdmin { Id = Guid.NewGuid(), AppId = app.Id, UserId = _owner, Role = AppAdminRole.Owner, GrantedAt = now },
            new AppAdmin { Id = Guid.NewGuid(), AppId = app.Id, UserId = _admin, Role = AppAdminRole.Admin, GrantedAt = now });
        await db.SaveChangesAsync();
        _app = app.Id;
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
    public async Task TheOwner_Rotates_WithoutABreak()
    {
        NewClientSecret made = await Secrets().CreateAsync(_owner, _app);
        Assert.True(made.Succeeded, made.Message);
        Assert.StartsWith("sgs_", made.Secret, StringComparison.Ordinal);

        // Both work while the servers move over.
        Assert.True(await AcceptsAsync(IssuedSecret));
        Assert.True(await AcceptsAsync(made.Secret!));
        Assert.False(await AcceptsAsync("sgs_not-a-secret"));

        ClientSecretsView view = (await Secrets().GetAsync(_owner, _app))!;
        Assert.True(view.CanManage);
        Assert.Equal(2, view.LiveCount);
        ClientSecretRow row = Assert.Single(view.Owned);
        Assert.Equal(made.Secret![..8] + "…" + made.Secret[^3..], row.Hint);
        Assert.NotNull(row.LastUsedAt);

        // The secret itself is never stored.
        await using (SangamDbContext db = _pg.CreateContext())
        {
            ClientSecret stored = await db.ClientSecrets.AsNoTracking().SingleAsync();
            Assert.DoesNotContain(made.Secret, stored.Hash, StringComparison.Ordinal);
        }

        // Then the owner retires the issued one.
        PartnerResult retired = await Secrets().RevokeAsync(_owner, _app, null);
        Assert.True(retired.Succeeded, retired.Message);
        Assert.False(await AcceptsAsync(IssuedSecret));
        Assert.True(await AcceptsAsync(made.Secret));
        Assert.Null((await Secrets().GetAsync(_owner, _app))!.Issued);

        // The last live secret cannot be revoked.
        Assert.False((await Secrets().RevokeAsync(_owner, _app, row.Id)).Succeeded);

        await using SangamDbContext audit = _pg.CreateContext();
        Assert.Equal(1, await audit.AuditEvents.CountAsync(e => e.Action == AuditActions.AppSecretCreate && e.ActorUserId == _owner));
        Assert.Equal(1, await audit.AuditEvents.CountAsync(e => e.Action == AuditActions.AppSecretRevoke && e.ActorUserId == _owner));
        List<string> metadata = await audit.AuditEvents.AsNoTracking().Select(e => e.Metadata).ToListAsync();
        Assert.DoesNotContain(metadata, m => m.Contains(made.Secret, StringComparison.Ordinal));
    }

    [PostgresFact]
    public async Task AtMostTwo_AreLive_AndRevokedOrExpiredOnes_AreRefused()
    {
        NewClientSecret first = await Secrets().CreateAsync(_owner, _app);
        Assert.True(first.Succeeded, first.Message);
        NewClientSecret third = await Secrets().CreateAsync(_owner, _app);
        Assert.False(third.Succeeded);
        Assert.Null(third.Secret);

        // Revoke the issued one; make a second of the owner's own; revoke the first.
        Assert.True((await Secrets().RevokeAsync(_owner, _app, null)).Succeeded);
        NewClientSecret second = await Secrets().CreateAsync(_owner, _app);
        Assert.True(second.Succeeded, second.Message);
        Guid firstId = (await Secrets().GetAsync(_owner, _app))!.Owned.Single(r => r.Hint.StartsWith(first.Secret![..8], StringComparison.Ordinal)).Id;
        Assert.True((await Secrets().RevokeAsync(_owner, _app, firstId)).Succeeded);
        Assert.False(await AcceptsAsync(first.Secret!));
        Assert.True(await AcceptsAsync(second.Secret!));

        // Twelve months on, it stops working by itself.
        await using (SangamDbContext db = _pg.CreateContext())
        {
            ClientSecret live = await db.ClientSecrets.SingleAsync(s => s.RevokedAt == null);
            live.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();
        }

        Assert.False(await AcceptsAsync(second.Secret!));
    }

    [PostgresFact]
    public async Task OnlyTheOwner_MakesOrRevokesSecrets()
    {
        ClientSecretsView view = (await Secrets().GetAsync(_admin, _app))!;
        Assert.False(view.CanManage);
        Assert.NotNull(view.Issued);

        NewClientSecret refused = await Secrets().CreateAsync(_admin, _app);
        Assert.False(refused.Succeeded);
        Assert.Null(refused.Secret);
        Assert.False((await Secrets().RevokeAsync(_admin, _app, null)).Succeeded);
        Assert.Null(await Secrets().GetAsync(Guid.NewGuid(), _app));
        Assert.True(await AcceptsAsync(IssuedSecret));
    }

    [PostgresFact]
    public async Task ARestart_DoesNotUndoTheOwnersRotation_ButANewIssuedSecretDoes()
    {
        NewClientSecret made = await Secrets().CreateAsync(_owner, _app);
        Assert.True((await Secrets().RevokeAsync(_owner, _app, null)).Succeeded);

        // A restart with the same settings leaves the retired secret retired.
        await RegisterAsync(IssuedSecret);
        Assert.False(await AcceptsAsync(IssuedSecret));
        Assert.True(await AcceptsAsync(made.Secret!));

        // A deployment that sets a new secret on purpose brings the issued secret back.
        const string Replaced = "replaced-secret-0123456789abcdefghijklmno";
        await RegisterAsync(Replaced);
        Assert.True(await AcceptsAsync(Replaced));
        Assert.True(await AcceptsAsync(made.Secret!));
    }

    [PostgresFact]
    public async Task ADisabledApplication_GetsNoTokens_WithAnySecret()
    {
        // rc.11: disabling is the operator's emergency stop when the owner cannot be reached.
        NewClientSecret made = await Secrets().CreateAsync(_owner, _app);
        await using (SangamDbContext db = _pg.CreateContext())
        {
            (await db.Apps.SingleAsync(a => a.Id == _app)).Status = AppStatus.Disabled;
            await db.SaveChangesAsync();
        }

        Assert.False(await AcceptsAsync(IssuedSecret));
        Assert.False(await AcceptsAsync(made.Secret!));

        await using (SangamDbContext db = _pg.CreateContext())
        {
            (await db.Apps.SingleAsync(a => a.Id == _app)).Status = AppStatus.Active;
            await db.SaveChangesAsync();
        }

        Assert.True(await AcceptsAsync(IssuedSecret));
        Assert.True(await AcceptsAsync(made.Secret!));
    }

    [PostgresFact]
    public async Task TheOwner_MovesToAKey_ThenDropsEverySecret_ButNotTheLastWayIn()
    {
        // rc.15 (ASVS V2.10.1): a public key, then the issued secret revoked; the key is now the only way in.
        using RSA rsa = RSA.Create(3072);
        PartnerResult added = await Secrets().AddKeyAsync(_owner, _app, rsa.ExportSubjectPublicKeyInfoPem(), "  HIS servers  ");
        Assert.True(added.Succeeded, added.Message);
        ClientSecretsView view = (await Secrets().GetAsync(_admin, _app))!;
        ClientKeyRow key = Assert.Single(view.Keys);
        Assert.Equal("HIS servers", key.Name);
        Assert.Equal("RSA 3072", key.Kind);
        Assert.Equal(2, view.CredentialCount);

        // The token endpoint asks OpenIddict for the application's keys.
        Assert.Equal(key.KeyId, Assert.Single(await KeysAsync()));

        // Not until Sangam has seen the application sign in with the key.
        PartnerResult tooSoon = await Secrets().RevokeAsync(_owner, _app, null);
        Assert.False(tooSoon.Succeeded);
        Assert.Contains("not yet seen", tooSoon.Message, StringComparison.Ordinal);
        await MarkUsedAsync(key.Id);
        Assert.True((await Secrets().RevokeAsync(_owner, _app, null)).Succeeded);
        Assert.False(await AcceptsAsync(IssuedSecret));
        Assert.False((await Secrets().RemoveKeyAsync(_owner, _app, key.Id)).Succeeded);

        // A second key, then the first removed once the servers use the second: a key rotation with no break.
        using ECDsa ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Assert.True((await Secrets().AddKeyAsync(_owner, _app, ec.ExportSubjectPublicKeyInfoPem(), null)).Succeeded);
        Assert.False((await Secrets().AddKeyAsync(_owner, _app, ec.ExportSubjectPublicKeyInfoPem(), null)).Succeeded);
        Assert.False((await Secrets().RemoveKeyAsync(_owner, _app, key.Id)).Succeeded);
        await MarkUsedAsync((await Secrets().GetAsync(_owner, _app))!.Keys.Single(k => k.Id != key.Id).Id);
        Assert.True((await Secrets().RemoveKeyAsync(_owner, _app, key.Id)).Succeeded);
        ClientKeyRow second = Assert.Single((await Secrets().GetAsync(_owner, _app))!.Keys);
        Assert.Equal("EC P-256", second.Name);
        Assert.Equal(second.KeyId, Assert.Single(await KeysAsync()));

        await using SangamDbContext audit = _pg.CreateContext();
        Assert.Equal(2, await audit.AuditEvents.CountAsync(e => e.Action == AuditActions.AppKeyAdd && e.ActorUserId == _owner));
        Assert.Equal(1, await audit.AuditEvents.CountAsync(e => e.Action == AuditActions.AppKeyRemove && e.ActorUserId == _owner));
    }

    [PostgresFact]
    public async Task OnlyTheOwner_AddsKeys_AtMostTwo_AndNeverAPrivateKey()
    {
        using RSA rsa = RSA.Create(2048);
        Assert.False((await Secrets().AddKeyAsync(_admin, _app, rsa.ExportSubjectPublicKeyInfoPem(), null)).Succeeded);

        PartnerResult privateKey = await Secrets().AddKeyAsync(_owner, _app, rsa.ExportPkcs8PrivateKeyPem(), null);
        Assert.False(privateKey.Succeeded);
        Assert.Contains("private key", privateKey.Message, StringComparison.Ordinal);

        using RSA weak = RSA.Create(1024);
        Assert.False((await Secrets().AddKeyAsync(_owner, _app, weak.ExportSubjectPublicKeyInfoPem(), null)).Succeeded);

        using RSA second = RSA.Create(2048);
        using RSA third = RSA.Create(2048);
        Assert.True((await Secrets().AddKeyAsync(_owner, _app, rsa.ExportSubjectPublicKeyInfoPem(), null)).Succeeded);
        Assert.True((await Secrets().AddKeyAsync(_owner, _app, second.ExportSubjectPublicKeyInfoPem(), null)).Succeeded);
        Assert.False((await Secrets().AddKeyAsync(_owner, _app, third.ExportSubjectPublicKeyInfoPem(), null)).Succeeded);

        // A disabled application has no keys.
        await using (SangamDbContext db = _pg.CreateContext())
        {
            (await db.Apps.SingleAsync(a => a.Id == _app)).Status = AppStatus.Disabled;
            await db.SaveChangesAsync();
        }

        Assert.Empty(await KeysAsync());
    }

    [PostgresFact]
    public async Task Owners_AreToldToReplace_AnOldIssuedSecret_Monthly_AndAnEndingSecret_Once()
    {
        // rc.15 (ASVS V2.10.1): the issued secret is over twelve months old.
        await using (SangamDbContext db = _pg.CreateContext())
        {
            App app = await db.Apps.SingleAsync(a => a.Id == _app);
            app.SecretSetAt = DateTimeOffset.UtcNow.AddDays(-400);
            await db.SaveChangesAsync();
        }

        Assert.Equal(1, await RemindAsync());
        Assert.Equal(1, Reminders("owner@lipi.test"));
        Assert.Equal(0, Reminders("admin@lipi.test"));
        Assert.Contains("over twelve months old", Outbox().LatestFor("owner@lipi.test")!.Message.TextBody, StringComparison.Ordinal);
        Assert.Contains($"/apps/{_app:D}/credentials", Outbox().LatestFor("owner@lipi.test")!.Message.TextBody, StringComparison.Ordinal);

        // Not again the same month; again a month later.
        Assert.Equal(0, await RemindAsync());
        await using (SangamDbContext db = _pg.CreateContext())
        {
            (await db.Apps.SingleAsync(a => a.Id == _app)).IssuedSecretReminderAt = DateTimeOffset.UtcNow.AddDays(-31);
            await db.SaveChangesAsync();
        }

        Assert.Equal(1, await RemindAsync());
        Assert.Equal(2, Reminders("owner@lipi.test"));

        // Replaced: a secret of the owner's own, the issued one retired. Nothing until the new one nears its end.
        NewClientSecret made = await Secrets().CreateAsync(_owner, _app);
        Assert.True((await Secrets().RevokeAsync(_owner, _app, null)).Succeeded);
        await using (SangamDbContext db = _pg.CreateContext())
        {
            (await db.Apps.SingleAsync(a => a.Id == _app)).IssuedSecretReminderAt = DateTimeOffset.UtcNow.AddDays(-31);
            await db.SaveChangesAsync();
        }

        Assert.Equal(0, await RemindAsync());

        await using (SangamDbContext db = _pg.CreateContext())
        {
            (await db.ClientSecrets.SingleAsync()).ExpiresAt = DateTimeOffset.UtcNow.AddDays(10);
            await db.SaveChangesAsync();
        }

        Assert.Equal(1, await RemindAsync());
        Assert.Equal(0, await RemindAsync());
        Assert.Equal(3, Reminders("owner@lipi.test"));
        string body = Outbox().LatestFor("owner@lipi.test")!.Message.TextBody;
        Assert.Contains(made.Secret![..8], body, StringComparison.Ordinal);
        Assert.DoesNotContain(made.Secret, body, StringComparison.Ordinal);

        await using SangamDbContext audit = _pg.CreateContext();
        Assert.Equal(3, await audit.AuditEvents.CountAsync(e => e.Action == AuditActions.AppSecretReminder && e.TargetId == _app));
    }

    private async Task<int> RemindAsync()
    {
        using IServiceScope scope = _provider.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<Sangam.Identity.Infrastructure.Maintenance.ClientSecretReminders>().RunAsync();
    }

    private InMemoryEmailOutbox Outbox() => _provider.GetRequiredService<InMemoryEmailOutbox>();

    private int Reminders(string email)
        => Outbox().Recent.Count(m => m.Message.ToEmail.Equals(email, StringComparison.OrdinalIgnoreCase) && m.Message.Subject.Contains("replace its client secret", StringComparison.Ordinal));

    private async Task MarkUsedAsync(Guid keyId)
    {
        await using SangamDbContext db = _pg.CreateContext();
        await db.ClientKeys.Where(k => k.Id == keyId).ExecuteUpdateAsync(u => u.SetProperty(k => k.LastUsedAt, DateTimeOffset.UtcNow));
    }

    private async Task<List<string>> KeysAsync()
    {
        using IServiceScope scope = _provider.CreateScope();
        IOpenIddictApplicationManager clients = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
        object app = (await clients.FindByClientIdAsync(ClientId))!;
        Microsoft.IdentityModel.Tokens.JsonWebKeySet? set = await clients.GetJsonWebKeySetAsync(app);
        return [.. (set?.Keys ?? []).Select(k => k.Kid)];
    }

    private IClientSecretService Secrets()
    {
        IServiceScope scope = _provider.CreateScope();
        _scopes.Add(scope);
        return scope.ServiceProvider.GetRequiredService<IClientSecretService>();
    }

    private async Task<bool> AcceptsAsync(string secret)
    {
        using IServiceScope scope = _provider.CreateScope();
        IOpenIddictApplicationManager clients = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
        object app = (await clients.FindByClientIdAsync(ClientId))!;
        return await clients.ValidateClientSecretAsync(app, secret);
    }

    private async Task RegisterAsync(string secret)
    {
        IConfiguration settings = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Sangam:Clients:his:ClientId"] = ClientId,
            ["Sangam:Clients:his:BaseUrl"] = "https://his.lipi.test/",
            ["Sangam:Clients:his:Secret"] = secret,
        }).Build();
        using IServiceScope scope = _provider.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ClientRegistration>().ApplyAsync(ClientRegistration.Read(settings));
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
