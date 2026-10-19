using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Sangam.Identity.Application.Abstractions;
using Sangam.Identity.Domain.Entities;
using Sangam.Identity.Infrastructure.Persistence;

namespace Sangam.Identity.Infrastructure.Provisioning;

/// <summary>
/// Hands each waiting <see cref="AppEvent"/> to its consumers, in the order the events were written: SCIM (one waiting
/// delivery per person and application at most, since each brings the person to the state of the moment) and each
/// webhook endpoint subscribed to the event type (one delivery each, with the exact body it will be sent).
/// </summary>
public sealed class AppEventDispatcher
{
    private readonly SangamDbContext _db;
    private readonly IClock _clock;

    /// <summary>Initialises the dispatcher.</summary>
    /// <param name="db">Database.</param>
    /// <param name="clock">Clock.</param>
    public AppEventDispatcher(SangamDbContext db, IClock clock)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <summary>Hands out waiting events; returns how many.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<int> DispatchAsync(CancellationToken cancellationToken = default)
    {
        List<AppEvent> events = await _db.AppEvents.Where(e => e.DispatchedAt == null).OrderBy(e => e.Sequence).Take(500).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (events.Count == 0)
        {
            return 0;
        }

        DateTimeOffset now = _clock.UtcNow;
        List<Guid> apps = [.. events.Select(e => e.AppId).Distinct()];
        HashSet<Guid> scimApps = [.. await _db.ScimTargets.AsNoTracking().Where(t => t.Enabled && apps.Contains(t.AppId)).Select(t => t.AppId).ToListAsync(cancellationToken).ConfigureAwait(false)];
        HashSet<(Guid, Guid?)> waiting = [.. (await _db.ScimDeliveries.AsNoTracking().Where(d => d.Status == "pending" && apps.Contains(d.AppId)).Select(d => new { d.AppId, d.UserId }).ToListAsync(cancellationToken).ConfigureAwait(false)).Select(d => (d.AppId, d.UserId))];
        List<WebhookEndpoint> endpoints = await _db.WebhookEndpoints.AsNoTracking().Where(e => e.Enabled && apps.Contains(e.AppId)).ToListAsync(cancellationToken).ConfigureAwait(false);
        Dictionary<Guid, string> clientIds = await _db.Apps.AsNoTracking().Where(a => apps.Contains(a.Id)).ToDictionaryAsync(a => a.Id, a => a.ClientId, cancellationToken).ConfigureAwait(false);
        HashSet<Guid> platform = [.. await _db.Apps.AsNoTracking().Where(a => apps.Contains(a.Id) && a.IsPlatform).Select(a => a.Id).ToListAsync(cancellationToken).ConfigureAwait(false)];
        List<Guid> orgs = [.. events.Where(e => e.OrgId != null).Select(e => e.OrgId!.Value).Distinct()];
        Dictionary<Guid, string> paths = await _db.Organisations.AsNoTracking().Where(o => orgs.Contains(o.Id)).ToDictionaryAsync(o => o.Id, o => o.Path, cancellationToken).ConfigureAwait(false);

        // rc.14 (decision record 0017): each application is told about people by its own id for them.
        Dictionary<Guid, IReadOnlyDictionary<Guid, Guid>> subjects = [];
        foreach (IGrouping<Guid, AppEvent> forApp in events.Where(e => endpoints.Any(x => x.AppId == e.AppId)).GroupBy(e => e.AppId))
        {
            List<Guid> people = [.. forApp.SelectMany(PeopleIn).Distinct()];
            subjects[forApp.Key] = await Apps.PairwiseSubjects.ForManyAsync(_db, forApp.Key, people, cancellationToken).ConfigureAwait(false);
        }

        foreach (AppEvent e in events)
        {
            e.DispatchedAt = now;
            if (scimApps.Contains(e.AppId) && e.Type is not (AppEventTypes.SessionRevoked or AppEventTypes.Ping))
            {
                // A role renamed or retired changes groups, not one person; everything else is about the person.
                Guid? person = e.Type == AppEventTypes.RoleChanged ? null : e.UserId;
                if ((person is not null || e.Type == AppEventTypes.RoleChanged) && waiting.Add((e.AppId, person)))
                {
                    _db.ScimDeliveries.Add(new ScimDelivery { AppId = e.AppId, UserId = person, Reason = person is null ? "groups" : e.Type, CreatedAt = now, NextAttemptAt = now });
                }
            }

            foreach (WebhookEndpoint endpoint in endpoints.Where(x => x.AppId == e.AppId && Subscribed(x, e.Type)))
            {
                _db.WebhookDeliveries.Add(new WebhookDelivery
                {
                    EndpointId = endpoint.Id,
                    AppId = e.AppId,
                    EventId = e.Id,
                    EventType = e.Type,
                    Payload = Payload(e, clientIds.GetValueOrDefault(e.AppId) ?? e.AppId.ToString("D"), e.OrgId is Guid org ? paths.GetValueOrDefault(org) : null, subjects.GetValueOrDefault(e.AppId), platform.Contains(e.AppId)),
                    CreatedAt = now,
                    NextAttemptAt = now,
                });
            }
        }

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return events.Count;
    }

    /// <summary>The webhook body for an event (SGM-217 §3): ids and codes only, never personal data.</summary>
    /// <param name="e">The event.</param>
    /// <param name="clientId">The application's client id.</param>
    /// <param name="orgPath">The organisation's path, if any.</param>
    /// <param name="subjects">rc.14: Sangam id → the application's own id for each person the event names; people missing from it are left out.</param>
    /// <param name="platform">Whether the application is one of Sangam's own (then a session keeps its own id).</param>
    public static string Payload(AppEvent e, string clientId, string? orgPath, IReadOnlyDictionary<Guid, Guid>? subjects = null, bool platform = false)
    {
        ArgumentNullException.ThrowIfNull(e);
        JsonObject body = new()
        {
            ["id"] = "evt_" + e.Id.ToString("N"),
            ["type"] = e.Type,
            ["created_at"] = e.CreatedAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture),
            ["app_id"] = clientId,
        };
        if (e.OrgId is Guid org)
        {
            body["tenant"] = new JsonObject { ["org_id"] = org.ToString("D"), ["org_path"] = orgPath };
        }

        if (e.UserId is Guid user && subjects?.GetValueOrDefault(user) is Guid sub)
        {
            body["subject"] = new JsonObject { ["sub"] = sub.ToString("D") };
        }

        JsonNode? data = JsonNode.Parse(string.IsNullOrWhiteSpace(e.Data) ? "{}" : e.Data);
        if (data is JsonObject fields)
        {
            foreach (string key in PersonFields)
            {
                if (fields[key] is JsonValue value && Guid.TryParse(value.ToString(), out Guid person))
                {
                    // A person the application has no id for is never named to it.
                    fields[key] = subjects?.GetValueOrDefault(person) is Guid mapped ? mapped.ToString("D") : null;
                }
            }

            // rc.14: a session is named by the application's own sid for it, as in its tokens.
            if (fields["sid"] is JsonValue sid && Guid.TryParse(sid.ToString(), out Guid session))
            {
                fields["sid"] = Application.Apps.AppSessionIds.For(session, e.AppId, platform).ToString("D");
            }
        }

        body["data"] = data;
        return body.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
    }

    /// <summary>rc.14: fields in an event's <c>data</c> that name a person (account merge), translated like <c>subject.sub</c>.</summary>
    private static readonly string[] PersonFields = ["merged_into", "merged_from"];

    /// <summary>The people an event names: its subject and any person fields in its data.</summary>
    private static IEnumerable<Guid> PeopleIn(AppEvent e)
    {
        if (e.UserId is Guid user)
        {
            yield return user;
        }

        if (string.IsNullOrWhiteSpace(e.Data))
        {
            yield break;
        }

        JsonObject? fields = JsonNode.Parse(e.Data) as JsonObject;
        foreach (string key in PersonFields)
        {
            if (fields?[key] is JsonValue value && Guid.TryParse(value.ToString(), out Guid person))
            {
                yield return person;
            }
        }
    }

    /// <summary>Whether an endpoint receives an event type (a test ping always goes to the endpoint it was sent for).</summary>
    /// <param name="endpoint">The endpoint.</param>
    /// <param name="type">The event type.</param>
    public static bool Subscribed(WebhookEndpoint endpoint, string type)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        return type != AppEventTypes.Ping && endpoint.Events.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Contains(type);
    }
}
