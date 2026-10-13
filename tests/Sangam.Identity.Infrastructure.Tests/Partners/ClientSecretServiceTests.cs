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
