using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Sangam.Identity.Application.Abstractions;
using Sangam.Identity.Domain;
using Sangam.Identity.Domain.Enums;
using Sangam.Identity.Infrastructure.Maintenance;
using Sangam.Identity.Infrastructure.Persistence;
using Sangam.Identity.Infrastructure.Services;
using Sangam.Identity.Infrastructure.Tests.Postgres;

namespace Sangam.Identity.Infrastructure.Tests.Persistence;

/// <summary>OI-039: audit retention is configurable and off by default; a purge keeps the chain verifiable.</summary>
[Collection("postgres")]
public sealed class AuditRetentionTests : IAsyncLifetime
{
    private readonly PostgresFixture _pg;

    public AuditRetentionTests(PostgresFixture pg)
    {
        _pg = pg;
    }

    public Task InitializeAsync() => _pg.IsAvailable ? _pg.ResetAsync() : Task.CompletedTask;

    public Task DisposeAsync() => Task.CompletedTask;

    [PostgresFact]
    public async Task WithoutARetentionSetting_NothingIsPurged()
    {
        await SeedAsync();
        using ServiceProvider provider = Provider(retentionDays: null);

        int removed = await Purge(provider).PurgeAuditAsync();

        Assert.Equal(0, removed);
        await using SangamDbContext db = _pg.CreateContext();
        Assert.Equal(3, await db.AuditEvents.CountAsync());
    }

    [PostgresFact]
    public async Task WithARetentionSetting_OldEventsGo_ThePurgeIsAudited_AndTheChainStillVerifies()
    {
        await SeedAsync();
        using ServiceProvider provider = Provider(retentionDays: "365");

        int removed = await Purge(provider).PurgeAuditAsync();

        Assert.Equal(2, removed);
        await using SangamDbContext db = _pg.CreateContext();
        List<string> actions = await db.AuditEvents.AsNoTracking().OrderBy(e => e.Id).Select(e => e.Action).ToListAsync();
        Assert.Equal([AuditActions.UserLoginSuccess, AuditActions.AuditRetentionPurge], actions);
        Assert.Null(await AuditChain.VerifyAsync(db));
    }

    private static AccountPurgeService Purge(ServiceProvider provider)
    {
        return provider.GetServices<IHostedService>().OfType<AccountPurgeService>().Single();
    }

    private ServiceProvider Provider(string? retentionDays)
    {
        ServiceCollection services = new();
        services.AddLogging();
        services.AddDataProtection();
        Dictionary<string, string?> settings = new()
        {
            ["ConnectionStrings:Sangam"] = _pg.ConnectionString,
            ["Sangam:Email:UseOutbox"] = "true",
            ["Sangam:Maintenance:Enabled"] = "false",
        };
        if (retentionDays is not null)
        {
            settings[AccountPurgeService.AuditRetentionKey] = retentionDays;
        }

        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        services.AddSingleton(configuration);
        services.AddSangamInfrastructure(configuration);
        return services.BuildServiceProvider();
    }

    private async Task SeedAsync()
    {
        FixtureContextFactory factory = new(_pg);
        EfAuditWriter old = new(factory, new FixedClock(DateTimeOffset.UtcNow.AddDays(-400)));
        await old.WriteAsync(new AuditEntry(AuditActions.UserRegister, AuditActorType.User, Guid.NewGuid()));
        await old.WriteAsync(new AuditEntry(AuditActions.UserEmailVerify, AuditActorType.User, Guid.NewGuid()));
        await new EfAuditWriter(factory, new FixedClock(DateTimeOffset.UtcNow)).WriteAsync(new AuditEntry(AuditActions.UserLoginSuccess, AuditActorType.User, Guid.NewGuid()));
    }

    private sealed class FixedClock : IClock
    {
        public FixedClock(DateTimeOffset now)
        {
            UtcNow = now;
        }

        public DateTimeOffset UtcNow { get; }
    }

    private sealed class FixtureContextFactory : IDbContextFactory<SangamDbContext>
    {
        private readonly PostgresFixture _pg;

        public FixtureContextFactory(PostgresFixture pg)
        {
            _pg = pg;
        }

        public SangamDbContext CreateDbContext() => _pg.CreateContext();
    }
}
