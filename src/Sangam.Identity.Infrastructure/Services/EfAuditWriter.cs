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
            db.AuditEvents.Add(new AuditEvent
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
                OccurredAt = _clock.UtcNow,
            });
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
