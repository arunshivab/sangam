using Microsoft.EntityFrameworkCore;
using Sangam.Identity.Application.Abstractions;
using Sangam.Identity.Domain;
using Sangam.Identity.Domain.Entities;
using Sangam.Identity.Domain.Enums;
using Sangam.Identity.Infrastructure.Persistence;
using Sangam.Identity.Infrastructure.Services;
using Sangam.Identity.Infrastructure.Tests.Postgres;

namespace Sangam.Identity.Infrastructure.Tests.Persistence;

[Collection("postgres")]
public sealed class EfAuditWriterTests : IAsyncLifetime
{
    private static readonly DateTimeOffset FixedNow = new(2026, 9, 24, 10, 30, 0, TimeSpan.Zero);
    private readonly PostgresFixture _pg;

    public EfAuditWriterTests(PostgresFixture pg)
    {
        _pg = pg;
    }

    public Task InitializeAsync() => _pg.IsAvailable ? _pg.ResetAsync() : Task.CompletedTask;

    public Task DisposeAsync() => Task.CompletedTask;

    [PostgresFact]
    public async Task WriteAsync_AppendsRowWithClockTimestamp()
    {
        EfAuditWriter writer = new(new FixtureContextFactory(_pg), new FixedClock(FixedNow));
        Guid target = Guid.NewGuid();

        await writer.WriteAsync(new AuditEntry(AuditActions.UserLoginFail, AuditActorType.Anonymous, TargetId: target, IpAddress: "203.0.113.7", Metadata: "{\"reason\":\"unknown_email\"}"));

        await using SangamDbContext db = _pg.CreateContext();
        AuditEvent row = await db.AuditEvents.SingleAsync();
        Assert.Equal("user.login.fail", row.Action);
        Assert.Equal(AuditActorType.Anonymous, row.ActorType);
        Assert.Null(row.ActorUserId);
        Assert.Equal(target, row.TargetId);
        Assert.Equal("203.0.113.7", row.IpAddress);
        Assert.Equal(FixedNow, row.OccurredAt);
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
