using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Sangam.Identity.Domain;
using Sangam.Identity.Domain.Entities;
using Sangam.Identity.Infrastructure.Maintenance;
using Sangam.Identity.Infrastructure.Persistence;
using Sangam.Identity.Infrastructure.Tests.Accounts;
using Sangam.Identity.Infrastructure.Tests.Postgres;

namespace Sangam.Identity.Infrastructure.Tests.Maintenance;

/// <summary>
/// R7 (PR-33, OI-047): background rounds run in one process at a time, however many hosts or replicas run them —
/// the precondition for running two identity-server replicas.
/// </summary>
[Collection("postgres")]
public sealed class ClusterLockTests : IAsyncLifetime
{
    private readonly PostgresFixture _pg;
    private ServiceProvider _first = null!;
    private ServiceProvider _second = null!;

    public ClusterLockTests(PostgresFixture pg)
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
        _first = Host();
        _second = Host();
    }

    public async Task DisposeAsync()
    {
        if (_first is not null)
        {
            await _first.DisposeAsync();
            await _second.DisposeAsync();
        }
    }

    [PostgresFact]
    public async Task WhileOneProcessHoldsTheRound_AnotherSkipsIt_AndRunsItAfterwards()
    {
        await using SangamDbContext db = _pg.CreateContext();
        TaskCompletionSource inside = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<bool> holder = ClusterLock.TryRunAsync(db, ClusterLock.Maintenance, async _ =>
        {
            inside.SetResult();
            await release.Task;
        }, CancellationToken.None);
        Assert.Same(inside.Task, await Task.WhenAny(inside.Task, holder));

        bool ran = false;
        Task Mark(CancellationToken _)
        {
            ran = true;
            return Task.CompletedTask;
        }

        Assert.False(await ClusterLock.TryRunAsync(db, ClusterLock.Maintenance, Mark, CancellationToken.None));
        Assert.False(ran);
        Assert.True(await ClusterLock.TryRunAsync(db, ClusterLock.Provisioning, _ => Task.CompletedTask, CancellationToken.None));

        release.SetResult();
        Assert.True(await holder);
        Assert.True(await ClusterLock.TryRunAsync(db, ClusterLock.Maintenance, Mark, CancellationToken.None));
        Assert.True(ran);
    }

    [PostgresFact]
    public async Task TwoHostsSweepingAtOnce_PurgeADeletedAccountOnce()
    {
        Guid userId;
        await using (SangamDbContext db = _pg.CreateContext())
        {
            SangamUser user = TestUsers.New("leaving@cluster.test", "+919876507001");
            user.PurgeAfter = DateTimeOffset.UtcNow.AddMinutes(-1);
            db.Users.Add(user);
            await db.SaveChangesAsync();
            userId = user.Id;
        }

        // The sweep is held open by a lock taken here, as a second host would; then both hosts sweep.
        await using (NpgsqlConnection other = new(_pg.ConnectionString))
        {
            await other.OpenAsync();
            await using (NpgsqlCommand take = new("SELECT pg_advisory_lock(@k)", other))
            {
                take.Parameters.AddWithValue("k", ClusterLock.Maintenance);
                await take.ExecuteScalarAsync();
            }

            Assert.Equal(0, (await Purge(_first).RunOnceAsync()).Accounts);

            // Released explicitly: a pooled connection keeps a session lock until it is next used.
            await using NpgsqlCommand release = new("SELECT pg_advisory_unlock(@k)", other);
            release.Parameters.AddWithValue("k", ClusterLock.Maintenance);
            await release.ExecuteScalarAsync();
        }

        (int Accounts, int Sessions)[] both = await Task.WhenAll(Purge(_first).RunOnceAsync(), Purge(_second).RunOnceAsync());
        Assert.Equal(1, both.Sum(r => r.Accounts));

        await using SangamDbContext check = _pg.CreateContext();
        Assert.Equal(1, await check.AuditEvents.CountAsync(e => e.Action == AuditActions.UserAccountDeletionComplete && e.TargetId == userId));
    }

    private static AccountPurgeService Purge(ServiceProvider host)
        => host.GetServices<IHostedService>().OfType<AccountPurgeService>().Single();

    private ServiceProvider Host()
    {
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
        return services.BuildServiceProvider();
    }
}
