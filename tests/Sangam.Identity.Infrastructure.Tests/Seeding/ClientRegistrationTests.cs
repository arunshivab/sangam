using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Abstractions;
using Sangam.Identity.Domain;
using Sangam.Identity.Domain.Entities;
using Sangam.Identity.Infrastructure.Persistence;
using Sangam.Identity.Infrastructure.Seeding;
using Sangam.Identity.Infrastructure.Tests.Postgres;

namespace Sangam.Identity.Infrastructure.Tests.Seeding;

/// <summary>
/// R4 (D-I): a deployment registers its own applications — the portal, the consoles and the demo at
/// demo.sangamid.in — from settings, with exactly the configured addresses and the same secret files the hosts use.
/// </summary>
[Collection("postgres")]
public sealed class ClientRegistrationTests : IAsyncLifetime
{
    private const string PortalSecret = "portal-secret-0123456789abcdefghijklmnop";
    private const string DemoSecret = "demo-secret-0123456789abcdefghijklmnopqrs";
    private readonly PostgresFixture _pg;
    private ServiceProvider _provider = null!;

    public ClientRegistrationTests(PostgresFixture pg)
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
    }

    public async Task DisposeAsync()
    {
        if (_provider is not null)
        {
            await _provider.DisposeAsync();
        }
    }

    [Fact]
    public void Settings_ThatCannotBeRight_RefuseTheStart()
    {
        Assert.Empty(ClientRegistration.Read(Config()));
        Assert.Contains("https", Refusal(("Sangam:Clients:demo:BaseUrl", "http://demo.sangamid.in/"), ("Sangam:Clients:demo:Secret", DemoSecret)), StringComparison.Ordinal);
        Assert.Contains("32 characters", Refusal(("Sangam:Clients:demo:BaseUrl", "https://demo.sangamid.in/"), ("Sangam:Clients:demo:Secret", "short")), StringComparison.Ordinal);
        Assert.Contains("development value", Refusal(("Sangam:Clients:demo:BaseUrl", "https://demo.sangamid.in/"), ("Sangam:Clients:demo:Secret", "imagiqa-dev-secret-change-me-0123456789")), StringComparison.Ordinal);
        Assert.Contains("Kind", Refusal(("Sangam:Clients:demo:BaseUrl", "https://demo.sangamid.in/"), ("Sangam:Clients:demo:Secret", DemoSecret), ("Sangam:Clients:demo:Kind", "robot")), StringComparison.Ordinal);
    }

    [PostgresFact]
    public async Task TheDeploymentsApplications_AreRegistered_WithExactlyTheirAddresses_AndOnlyOnce()
    {
        IReadOnlyList<ClientSpec> specs = ClientRegistration.Read(Config(
            ("Sangam:Clients:portal:ClientId", "sangam-portal"), ("Sangam:Clients:portal:Kind", "portal"), ("Sangam:Clients:portal:BaseUrl", "https://account.sangamid.in"), ("Sangam:Clients:portal:Secret", PortalSecret),
            ("Sangam:Clients:demo:ClientId", "imagiqa"), ("Sangam:Clients:demo:BaseUrl", "https://demo.sangamid.in/"), ("Sangam:Clients:demo:Secret", DemoSecret),
            ("Sangam:Clients:demo:DisplayName", "imagiQa (demo)"), ("Sangam:Clients:demo:OwnerCompanyName", "imagiQa Healthcare Services Pvt Ltd")));

        Assert.Equal(2, await RegisterAsync(specs));
        Assert.Equal(0, await RegisterAsync(specs));

        using IServiceScope scope = _provider.CreateScope();
        IOpenIddictApplicationManager clients = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
        object demo = (await clients.FindByClientIdAsync("imagiqa"))!;
        Assert.Equal(["https://demo.sangamid.in/signin-sangam"], (await clients.GetRedirectUrisAsync(demo)).Select(u => u.ToString()));
        Assert.Equal(["https://demo.sangamid.in/signout-sangam"], (await clients.GetPostLogoutRedirectUrisAsync(demo)).Select(u => u.ToString()));
        Assert.True(await clients.ValidateClientSecretAsync(demo, DemoSecret));
        Assert.Contains(OpenIddictConstants.Permissions.Prefixes.Scope + "orgs.read", await clients.GetPermissionsAsync(demo));

        await using SangamDbContext db = _pg.CreateContext();
        App demoApp = await db.Apps.SingleAsync(a => a.ClientId == "imagiqa");
        Assert.Equal(("imagiQa (demo)", "imagiQa Healthcare Services Pvt Ltd", false), (demoApp.DisplayName, demoApp.OwnerCompanyName, demoApp.IsPlatform));
        App portal = await db.Apps.SingleAsync(a => a.ClientId == "sangam-portal");
        Assert.True(portal.IsPlatform);
        Assert.Equal(2, await db.AuditEvents.CountAsync(e => e.Action == AuditActions.SystemClientRegistered));
    }

    [PostgresFact]
    public async Task ARotatedSecret_AndDevelopmentAddresses_AreCorrected()
    {
        (string, string)[] demo = [("Sangam:Clients:demo:ClientId", "imagiqa"), ("Sangam:Clients:demo:BaseUrl", "https://demo.sangamid.in/")];
        await RegisterAsync(ClientRegistration.Read(Config([.. demo, ("Sangam:Clients:demo:Secret", DemoSecret)])));
        using (IServiceScope scope = _provider.CreateScope())
        {
            // Someone added a development address by hand.
            IOpenIddictApplicationManager clients = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
            object app = (await clients.FindByClientIdAsync("imagiqa"))!;
            OpenIddictApplicationDescriptor descriptor = new();
            await clients.PopulateAsync(descriptor, app);
            descriptor.RedirectUris.Add(new Uri("http://localhost:5500/signin-sangam"));
            await clients.UpdateAsync(app, descriptor);
        }

        string rotated = "rotated-demo-secret-0123456789abcdefghijk";
        Assert.Equal(1, await RegisterAsync(ClientRegistration.Read(Config([.. demo, ("Sangam:Clients:demo:Secret", rotated)]))));
        using IServiceScope after = _provider.CreateScope();
        IOpenIddictApplicationManager manager = after.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
        object registered = (await manager.FindByClientIdAsync("imagiqa"))!;
        Assert.True(await manager.ValidateClientSecretAsync(registered, rotated));
        Assert.False(await manager.ValidateClientSecretAsync(registered, DemoSecret));
        Assert.Single(await manager.GetRedirectUrisAsync(registered));
    }

    private async Task<int> RegisterAsync(IReadOnlyList<ClientSpec> specs)
    {
        using IServiceScope scope = _provider.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ClientRegistration>().ApplyAsync(specs);
    }

    private static string Refusal(params (string Key, string Value)[] settings)
        => Assert.Throws<InvalidOperationException>(() => ClientRegistration.Read(Config(settings))).Message;

    private static IConfiguration Config(params (string Key, string Value)[] settings)
        => new ConfigurationBuilder().AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value))).Build();
}
