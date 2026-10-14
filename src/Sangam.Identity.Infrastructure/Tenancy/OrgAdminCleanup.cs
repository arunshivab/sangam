using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Sangam.Identity.Application.Abstractions;
using Sangam.Identity.Domain;
using Sangam.Identity.Domain.Entities;
using Sangam.Identity.Domain.Enums;
using Sangam.Identity.Infrastructure.Persistence;

namespace Sangam.Identity.Infrastructure.Tenancy;

/// <summary>
/// rc.12: an admin right (customer, branch, or emergency access reviewer) lasts only while the person still has a place in
/// that tree. When their last place there ends (they left, their role expired, the customer closed), the right ends too,
/// so a former member never keeps console access.
/// </summary>
internal static class OrgAdminCleanup
{
    /// <summary>Ends the person's rights in an application where they no longer have a live place under them.</summary>
    /// <param name="db">Database.</param>
    /// <param name="audit">Audit writer.</param>
    /// <param name="appId">The application.</param>
    /// <param name="userId">The person.</param>
    /// <param name="now">The time.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>How many rights ended.</returns>
    public static async Task<int> RunAsync(SangamDbContext db, IAuditWriter audit, Guid appId, Guid userId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        List<OrgAdmin> rights = await db.OrgAdmins.Include(a => a.Org)
            .Where(a => a.AppId == appId && a.UserId == userId && a.RevokedAt == null)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        if (rights.Count == 0)
        {
            return 0;
        }

        List<string> places = await db.OrgMemberships.AsNoTracking()
            .Where(m => m.AppId == appId && m.UserId == userId && m.RevokedAt == null && (m.ExpiresAt == null || m.ExpiresAt > now))
            .Select(m => m.Org!.Path)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        List<OrgAdmin> orphaned = [.. rights.Where(r => r.Org is null || !places.Any(p => p.StartsWith(r.Org.Path, StringComparison.Ordinal)))];
        foreach (OrgAdmin right in orphaned)
        {
            right.RevokedAt = now;
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        foreach (OrgAdmin right in orphaned)
        {
            await audit.WriteAsync(
                new AuditEntry(AuditActions.OrgAdminRevoke, AuditActorType.System, ActorAppId: appId, TargetType: "user", TargetId: userId,
                    Metadata: JsonSerializer.Serialize(new Dictionary<string, object?> { ["org"] = right.OrgId, ["right"] = right.Right == OrgAdminRight.Admin ? "admin" : "emergency_reviewer", ["why"] = "no_place_left" })),
                cancellationToken).ConfigureAwait(false);
        }

        return orphaned.Count;
    }
}
