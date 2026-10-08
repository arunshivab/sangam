using Microsoft.EntityFrameworkCore;
using Sangam.Identity.Application.Abstractions;
using Sangam.Identity.Domain;
using Sangam.Identity.Domain.Entities;
using Sangam.Identity.Domain.Enums;
using Sangam.Identity.Infrastructure.Persistence;
using Sangam.Identity.Infrastructure.Services;
using Sangam.Identity.Infrastructure.Tests.Postgres;

namespace Sangam.Identity.Infrastructure.Tests.Persistence;

/// <summary>OI-039: the audit hash chain, its verifier, and client details on every entry.</summary>
[Collection("postgres")]
public sealed class AuditChainTests : IAsyncLifetime
{
    private readonly PostgresFixture _pg;

    public AuditChainTests(PostgresFixture pg)
    {
        _pg = pg;
    }

    public Task InitializeAsync() => _pg.IsAvailable ? _pg.ResetAsync() : Task.CompletedTask;

    public Task DisposeAsync() => Task.CompletedTask;

    [PostgresFact]
    public async Task Writer_ChainsEveryEvent_AndTheChainVerifies()
    {
        EfAuditWriter writer = new(new FixtureContextFactory(_pg), new SystemUtcClock());
        await writer.WriteAsync(new AuditEntry(AuditActions.UserRegister, AuditActorType.User, Guid.NewGuid()));
        await writer.WriteAsync(new AuditEntry(AuditActions.UserLoginSuccess, AuditActorType.User, Guid.NewGuid(), IpAddress: "203.0.113.7"));
        await writer.WriteAsync(new AuditEntry(AuditActions.UserLogoutApp, AuditActorType.User, Guid.NewGuid()));

        await using SangamDbContext db = _pg.CreateContext();
        List<AuditEvent> rows = await db.AuditEvents.AsNoTracking().OrderBy(e => e.Id).ToListAsync();
        Assert.Equal(3, rows.Count);
        Assert.Null(rows[0].PrevHash);
        Assert.Equal(rows[0].Hash, rows[1].PrevHash);
        Assert.Equal(rows[1].Hash, rows[2].PrevHash);
        Assert.Null(await AuditChain.VerifyAsync(db));
    }

    [PostgresFact]
    public async Task Chain_SurvivesJsonbNormalisationOfMetadata()
    {
        // jsonb reorders keys and drops whitespace on storage; the hash must not depend on either.
        EfAuditWriter writer = new(new FixtureContextFactory(_pg), new SystemUtcClock());
        await writer.WriteAsync(new AuditEntry(AuditActions.AppSettingsUpdate, AuditActorType.Admin, Guid.NewGuid(), Metadata: "{ \"zeta\": \"1\",  \"alpha\": { \"b\": 2, \"a\": [3, 1] } }"));
        await writer.WriteAsync(new AuditEntry(AuditActions.UserLoginSuccess, AuditActorType.User, Guid.NewGuid(), Metadata: "{\"reason\":\"ok\"}"));

        await using SangamDbContext db = _pg.CreateContext();
        Assert.Null(await AuditChain.VerifyAsync(db));
    }

    [PostgresFact]
    public async Task Verifier_FindsTheFirstTamperedEvent()
    {
        EfAuditWriter writer = new(new FixtureContextFactory(_pg), new SystemUtcClock());
        for (int i = 0; i < 3; i++)
        {
            await writer.WriteAsync(new AuditEntry(AuditActions.UserLoginSuccess, AuditActorType.User, Guid.NewGuid()));
        }

        await using SangamDbContext db = _pg.CreateContext();
        long second = await db.AuditEvents.OrderBy(e => e.Id).Skip(1).Select(e => e.Id).FirstAsync();
        await using (Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction tx = await db.Database.BeginTransactionAsync())
        {
            // Someone with the table owner's rights edits a row: the chain must show it.
            await db.Database.ExecuteSqlRawAsync("SELECT set_config('sangam.audit_maintenance', 'on', true);");
            await db.Database.ExecuteSqlRawAsync("UPDATE audit_events SET action = 'tampered' WHERE id = {0}", second);
            await tx.CommitAsync();
        }

        Assert.Equal(second, await AuditChain.VerifyAsync(db));
    }

    [PostgresFact]
    public async Task ClientAwareWriter_FillsMissingClientDetails_AndKeepsGivenOnes()
    {
        ClientContext client = new();
        client.Set("198.51.100.4", "Mozilla/5.0 test");
        ClientAwareAuditWriter writer = new(new EfAuditWriter(new FixtureContextFactory(_pg), new SystemUtcClock()), client);

        await writer.WriteAsync(new AuditEntry(AuditActions.AppSettingsUpdate, AuditActorType.Admin, Guid.NewGuid()));
        await writer.WriteAsync(new AuditEntry(AuditActions.UserLoginSuccess, AuditActorType.User, Guid.NewGuid(), IpAddress: "203.0.113.9", UserAgent: "given"));

        await using SangamDbContext db = _pg.CreateContext();
        List<AuditEvent> rows = await db.AuditEvents.AsNoTracking().OrderBy(e => e.Id).ToListAsync();
        Assert.Equal(("198.51.100.4", "Mozilla/5.0 test"), (rows[0].IpAddress, rows[0].UserAgent));
        Assert.Equal(("203.0.113.9", "given"), (rows[1].IpAddress, rows[1].UserAgent));
    }

    private sealed class SystemUtcClock : IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
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
