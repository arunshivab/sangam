using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Sangam.Identity.Domain.Entities;
using Sangam.Identity.Infrastructure.Persistence;

namespace Sangam.Identity.Infrastructure.Provisioning;

/// <summary>
/// Writes <see cref="AppEvent"/>s (SGM-217 §2) next to the change they describe, for SCIM provisioning and webhooks. An
/// event is written only for an application with a consumer switched on (a SCIM target or a webhook endpoint), so the
/// table holds nothing for applications that never asked. Events carry ids, never personal data.
/// </summary>
public static class AppEventLog
{
    /// <summary>
    /// Adds the event to the context without saving, so it is saved with the change itself. With no application given,
    /// it goes to every application the person has linked.
    /// </summary>
    /// <param name="db">The context the change is in.</param>
    /// <param name="type">The event type (<see cref="AppEventTypes"/>).</param>
    /// <param name="appId">The application, or <see langword="null"/> for every application the person has linked.</param>
    /// <param name="userId">The person, if any.</param>
    /// <param name="orgId">The organisation, if any.</param>
    /// <param name="data">Details: ids and codes only.</param>
    /// <param name="now">When it happened.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>How many events were added.</returns>
    public static async Task<int> AddAsync(SangamDbContext db, string type, Guid? appId, Guid? userId, Guid? orgId, IReadOnlyDictionary<string, object?>? data, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(type);
        List<Guid> apps = appId is Guid one
            ? [one]
            : userId is Guid person
                ? await db.AppGrants.AsNoTracking().Where(g => g.UserId == person && g.RevokedAt == null).Select(g => g.AppId).Distinct().ToListAsync(cancellationToken).ConfigureAwait(false)
                : [];
        if (apps.Count == 0)
        {
            return 0;
        }

        HashSet<Guid> listening = await ListeningAsync(db, apps, cancellationToken).ConfigureAwait(false);
        int added = 0;
        string json = JsonSerializer.Serialize(data ?? new Dictionary<string, object?>());
        foreach (Guid app in apps.Where(listening.Contains))
        {
            db.AppEvents.Add(new AppEvent
            {
                Id = Guid.CreateVersion7(now),
                AppId = app,
                Type = type,
                UserId = userId,
                OrgId = orgId,
                Data = json,
                CreatedAt = now,
            });
            added++;
        }

        return added;
    }

    /// <summary>Adds the event and saves it now (for changes made with a bulk update).</summary>
    /// <param name="db">The context.</param>
    /// <param name="type">The event type.</param>
    /// <param name="appId">The application, or every one the person has linked.</param>
    /// <param name="userId">The person.</param>
    /// <param name="orgId">The organisation.</param>
    /// <param name="data">Details.</param>
    /// <param name="now">When.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static async Task RaiseAsync(SangamDbContext db, string type, Guid? appId, Guid? userId, Guid? orgId, IReadOnlyDictionary<string, object?>? data, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        if (await AddAsync(db, type, appId, userId, orgId, data, now, cancellationToken).ConfigureAwait(false) > 0)
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>The applications among <paramref name="apps"/> with a consumer switched on.</summary>
    /// <param name="db">The context.</param>
    /// <param name="apps">Candidate applications.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    internal static async Task<HashSet<Guid>> ListeningAsync(SangamDbContext db, IReadOnlyCollection<Guid> apps, CancellationToken cancellationToken)
    {
        List<Guid> scim = await db.ScimTargets.AsNoTracking().Where(t => t.Enabled && apps.Contains(t.AppId)).Select(t => t.AppId).ToListAsync(cancellationToken).ConfigureAwait(false);
        List<Guid> hooks = await db.WebhookEndpoints.AsNoTracking().Where(e => e.Enabled && apps.Contains(e.AppId)).Select(e => e.AppId).ToListAsync(cancellationToken).ConfigureAwait(false);
        return [.. scim, .. hooks];
    }
}
