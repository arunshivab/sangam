using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Sangam.Identity.Infrastructure.Maintenance;
using Sangam.Identity.Infrastructure.Tests.Postgres;

namespace Sangam.Identity.Infrastructure.Tests.Maintenance;

/// <summary>rc.16 (ASVS V1.11.2): a second identity server against the same database refuses to start.</summary>
[Collection("postgres")]
public sealed class SingleInstanceGuardTests
{
    private readonly PostgresFixture _pg;

    public SingleInstanceGuardTests(PostgresFixture pg)
    {
        _pg = pg;
    }

    [PostgresFact]
    public async Task ASecondIdentityServer_RefusesToStart_UntilTheFirstStops()
    {
        await using SingleInstanceGuard first = Guard("Production");
        await using SingleInstanceGuard second = Guard("Production");

        await first.StartingAsync(CancellationToken.None);
        Assert.True(first.Holding);
        InvalidOperationException refused = await Assert.ThrowsAsync<InvalidOperationException>(() => second.StartingAsync(CancellationToken.None));
        Assert.Contains("Another identity server is already running", refused.Message, StringComparison.Ordinal);
        Assert.False(second.Holding);

        await first.StopAsync(CancellationToken.None);
        Assert.False(first.Holding);
        await second.StartingAsync(CancellationToken.None);
        Assert.True(second.Holding);
        await second.StopAsync(CancellationToken.None);
    }

    [PostgresFact]
    public async Task ALostConnection_IsRecovered_AndOnlyAnotherServerStopsThisOne()
    {
        await using SingleInstanceGuard first = Guard("Production");
        await first.StartingAsync(CancellationToken.None);
        int old = first.Backend;

        // The lock's connection is lost while its database session lives on (a broken network): the guard ends its own
        // old session and takes the lock again, instead of stopping as if another server held it.
        first.LoseConnectionForTests();
        Assert.True(await first.CheckOnceAsync());
        Assert.True(first.Holding);
        Assert.NotEqual(old, first.Backend);

        // A second server sees who holds the lock, and is refused.
        await using SingleInstanceGuard second = Guard("Production");
        Assert.Equal(first.Backend, await second.HolderAsync(CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => second.StartingAsync(CancellationToken.None));
        await first.StopAsync(CancellationToken.None);
    }

    [PostgresFact]
    public async Task TheGuard_IsOffInTesting_AndCanBeTurnedOff()
    {
        await using SingleInstanceGuard held = Guard("Production");
        await held.StartingAsync(CancellationToken.None);

        await using SingleInstanceGuard testing = Guard("Testing");
        await testing.StartingAsync(CancellationToken.None);
        Assert.False(testing.Holding);

        await using SingleInstanceGuard off = Guard("Production", enabled: false);
        await off.StartingAsync(CancellationToken.None);
        Assert.False(off.Holding);
        await held.StopAsync(CancellationToken.None);
    }

    private SingleInstanceGuard Guard(string environment, bool? enabled = null)
    {
        Dictionary<string, string?> settings = new() { ["ConnectionStrings:Sangam"] = _pg.ConnectionString };
        if (enabled is bool value)
        {
            settings[SingleInstanceGuard.EnabledKey] = value ? "true" : "false";
        }

        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        return new SingleInstanceGuard(configuration, new Environment(environment), new Lifetime(), NullLogger<SingleInstanceGuard>.Instance);
    }

    private sealed class Environment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;

        public string ApplicationName { get; set; } = "Sangam.Identity.Server";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class Lifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;

        public CancellationToken ApplicationStopping => CancellationToken.None;

        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication()
        {
        }
    }
}
