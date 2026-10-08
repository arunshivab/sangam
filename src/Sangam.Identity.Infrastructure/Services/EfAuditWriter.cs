using Microsoft.EntityFrameworkCore;
using Sangam.Identity.Application.Abstractions;
using Sangam.Identity.Domain.Entities;
using Sangam.Identity.Infrastructure.Persistence;

namespace Sangam.Identity.Infrastructure.Services;

/// <summary>
/// Appends audit rows through a dedicated <see cref="SangamDbContext"/> instance, so an audit
/// write is never lost when the caller's transaction rolls back and never delayed by it.
/// </summary>
public sealed class EfAuditWriter : IAuditWriter
{
    private readonly IDbContextFactory<SangamDbContext> _contextFactory;
    private readonly IClock _clock;

    /// <summary>Initialises the writer.</summary>
    /// <param name="contextFactory">Factory for a fresh context per write.</param>
    /// <param name="clock">Clock for the event timestamp.</param>
    public EfAuditWriter(IDbContextFactory<SangamDbContext> contextFactory, IClock clock)
    {
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <inheritdoc />
    public async Task WriteAsync(AuditEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        SangamDbContext db = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            // One writer at a time extends the chain (OI-039): take a transaction-scoped advisory
            // lock, read the newest hash, hash this event after it, insert, commit.
            Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using (tx.ConfigureAwait(false))
            {
                await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock({0})", [AuditChain.LockKey], cancellationToken).ConfigureAwait(false);
                string? previous = await db.AuditEvents.Where(e => e.Hash != null).OrderByDescending(e => e.Id).Select(e => e.Hash).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
                AuditEvent row = new()
                {
                    Action = entry.Action,
                    ActorType = entry.ActorType,
                    ActorUserId = entry.ActorUserId,
                    ActorAppId = entry.ActorAppId,
                    TargetType = entry.TargetType,
                    TargetId = entry.TargetId,
                    Metadata = entry.Metadata,
                    IpAddress = entry.IpAddress,
                    UserAgent = entry.UserAgent,
                    OccurredAt = AuditChain.ToStoredPrecision(_clock.UtcNow),
                    PrevHash = previous,
                };
                row.Hash = AuditChain.Compute(previous, row);
                db.AuditEvents.Add(row);
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
