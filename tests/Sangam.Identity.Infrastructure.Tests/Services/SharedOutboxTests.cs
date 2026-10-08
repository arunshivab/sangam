using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Sangam.Identity.Application.Abstractions;
using Sangam.Identity.Domain.Entities;
using Sangam.Identity.Infrastructure.Services;
using Sangam.Identity.Infrastructure.Tests.Postgres;

namespace Sangam.Identity.Infrastructure.Tests.Services;

/// <summary>
/// V-11: in Development the hosts share one outbox, so an e-mail the operator console raises shows on the identity
/// server's /dev/outbox; and (D-H) the founder's alert still goes out when the database cannot be reached.
/// </summary>
[Collection("postgres")]
public sealed class SharedOutboxTests : IAsyncLifetime
{
    private readonly PostgresFixture _pg;
    private readonly List<ServiceProvider> _providers = [];

    public SharedOutboxTests(PostgresFixture pg)
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

    public async Task DisposeAsync()
    {
        foreach (ServiceProvider provider in _providers)
        {
            await provider.DisposeAsync();
        }
    }

    [PostgresFact]
    public async Task AnEmailTheConsoleSends_IsOnTheIdentityServersOutbox()
    {
        ServiceProvider console = Host("admin", _pg.ConnectionString, shared: true);
        ServiceProvider identity = Host("identity", _pg.ConnectionString, shared: true);
        await console.GetRequiredService<IEmailSender>().SendAsync(new EmailMessage("lost-phone@example.in", "Meera", "Sangam support was asked to reset your two-step sign-in", "Cancel: http://localhost:5100/account/reset/cancel/abc"));
        await console.GetRequiredService<ISmsSender>().SendAsync(new OutgoingSms("+919000000777", "reset_notice", "1107000000000000002", "SANGAM", "Sangam support was asked to reset your two-step sign-in."));

        IReadOnlyList<DevOutboxMessage> mail = await identity.GetRequiredService<DevOutboxStore>().RecentAsync("email", 10);
        DevOutboxMessage message = Assert.Single(mail);
        Assert.Equal(("admin", "lost-phone@example.in"), (message.Host, message.Recipient));
        Assert.Contains("/account/reset/cancel/abc", message.Body, StringComparison.Ordinal);
        Assert.Equal("+919000000777", Assert.Single(await identity.GetRequiredService<DevOutboxStore>().RecentAsync("sms", 10)).Recipient);

        // The in-memory outbox the tests read keeps working as before.
        Assert.NotNull(console.GetRequiredService<InMemoryEmailOutbox>().LatestFor("lost-phone@example.in"));
    }

    [Fact]
    public void WithoutTheSetting_NoHostWritesToTheTable()
    {
        ServiceProvider host = Host("identity", "Host=127.0.0.1;Port=1;Database=x;Username=x;Password=x", shared: false);
        Assert.Null(host.GetService<DevOutboxStore>());
    }

    [Fact]
    public async Task WhenTheDatabaseIsDown_TheFoundersAlertStillGoesOut_InPlainText()
    {
        ServiceProvider host = Host("identity", "Host=127.0.0.1;Port=1;Database=x;Username=x;Password=x;Timeout=2", shared: false, ("Sangam:Alerts:Emails", "founder@example.in"));
        using IServiceScope scope = host.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IPlatformAlerts>().SendAsync("database not answering", "The identity server cannot reach PostgreSQL.");

        EmailMessage alert = host.GetRequiredService<InMemoryEmailOutbox>().LatestFor("founder@example.in")!.Message;
        Assert.Equal("Sangam alert: database not answering", alert.Subject);
        Assert.Contains("cannot reach PostgreSQL", alert.TextBody, StringComparison.Ordinal);
    }

    private ServiceProvider Host(string name, string connection, bool shared, params (string Key, string Value)[] extra)
    {
        Dictionary<string, string?> settings = new()
        {
            ["ConnectionStrings:Sangam"] = connection,
            ["Sangam:Email:UseOutbox"] = "true",
            ["Sangam:Email:SharedOutbox"] = shared ? "true" : "false",
            ["Sangam:Sms:Enabled"] = "true",
            ["Sangam:Sms:Provider"] = "outbox",
            ["Sangam:Maintenance:Enabled"] = "false",
            ["Sangam:DataProtection:PersistKeys"] = "false",
            ["Sangam:Monitoring:HostName"] = name,
        };
        foreach ((string key, string value) in extra)
        {
            settings[key] = value;
        }

        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        ServiceCollection services = new();
        services.AddLogging();
        services.AddDataProtection();
        services.AddSingleton(configuration);
        services.AddSangamInfrastructure(configuration);
        ServiceProvider provider = services.BuildServiceProvider();
        _providers.Add(provider);
        return provider;
    }
}
