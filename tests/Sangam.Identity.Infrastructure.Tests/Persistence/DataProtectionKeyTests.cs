using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Sangam.Identity.Infrastructure.Persistence;
using Sangam.Identity.Infrastructure.Tests.Postgres;

namespace Sangam.Identity.Infrastructure.Tests.Persistence;

/// <summary>OI-037: the data-protection key ring is kept in the database, so it survives a restart.</summary>
[Collection("postgres")]
public sealed class DataProtectionKeyTests : IAsyncLifetime
{
    private readonly PostgresFixture _pg;

    public DataProtectionKeyTests(PostgresFixture pg)
    {
        _pg = pg;
    }

    public Task InitializeAsync() => _pg.IsAvailable ? _pg.ResetAsync() : Task.CompletedTask;

    public Task DisposeAsync() => Task.CompletedTask;

    [PostgresFact]
    public async Task Keys_ArePersisted_AndASecondHostCanReadWhatTheFirstProtected()
    {
        string secret;
        using (ServiceProvider first = Host())
        {
            secret = first.GetRequiredService<IDataProtectionProvider>().CreateProtector("test").Protect("payload");
        }

        await using (SangamDbContext db = _pg.CreateContext())
        {
            Assert.True(await db.DataProtectionKeys.AnyAsync());
        }

        using ServiceProvider second = Host();
        Assert.Equal("payload", second.GetRequiredService<IDataProtectionProvider>().CreateProtector("test").Unprotect(secret));
    }

    private ServiceProvider Host()
    {
        ServiceCollection services = new();
        services.AddLogging();
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Sangam"] = _pg.ConnectionString,
            ["Sangam:Email:UseOutbox"] = "true",
            ["Sangam:Maintenance:Enabled"] = "false",
        }).Build();
        services.AddSingleton(configuration);
        services.AddSangamInfrastructure(configuration);
        return services.BuildServiceProvider();
    }
}
