using Microsoft.EntityFrameworkCore;
using Sangam.Identity.Domain.Entities;
using Sangam.Identity.Infrastructure.Persistence;

namespace Sangam.Identity.Infrastructure.Portal;

/// <summary>
/// Queues a back-channel logout for every application that took part in sessions that just ended and has a
/// back-channel logout address (PR-20). Any host may queue; the identity server delivers.
/// </summary>
internal static class LogoutQueue
{
    public static async Task EnqueueAsync(SangamDbContext db, IReadOnlyCollection<Guid> sessionIds, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (sessionIds.Count == 0)
        {
            return;
        }

        var targets = await db.SessionApps.AsNoTracking()
            .Where(sa => sessionIds.Contains(sa.SessionId))
            .Join(db.Apps.Where(a => a.BackChannelLogoutUri != null), sa => sa.AppId, a => a.Id, (sa, a) => new { sa.SessionId, sa.AppId, sa.UserId })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (targets.Count == 0)
        {
            return;
        }

        foreach (var target in targets)
        {
            db.LogoutNotifications.Add(new LogoutNotification
            {
                Id = Guid.NewGuid(),
                SessionId = target.SessionId,
                AppId = target.AppId,
                UserId = target.UserId,
                CreatedAt = now,
                NextAttemptAt = now,
            });
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
