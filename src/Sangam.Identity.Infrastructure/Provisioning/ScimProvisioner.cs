using System.Text.Json.Nodes;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sangam.Identity.Application.Abstractions;
using Sangam.Identity.Domain;
using Sangam.Identity.Domain.Entities;
using Sangam.Identity.Domain.Enums;
using Sangam.Identity.Infrastructure.Persistence;
using Sangam.Shared.Constants;

namespace Sangam.Identity.Infrastructure.Provisioning;

/// <summary>
/// SCIM 2.0 provisioning from Sangam to applications (PR-23, SGM-216). Events from the outbox become deliveries — at
/// most one waiting per person and application — and each delivery brings that person to the state Sangam holds
/// <em>now</em>, rather than replaying what happened: so deliveries can be retried, merged and run late without
/// getting the order wrong. A person is provisioned while they have a role in the application, their consent stands
/// and their account is active; otherwise they are deactivated (or deleted, if the application asked). Groups are one
/// per role, or one per role at each organisation. A nightly reconciliation queues everyone again and compares counts.
/// Runs on the identity server only.
/// </summary>
public sealed partial class ScimProvisioner
{
    /// <summary>The data-protection purpose for SCIM bearer tokens.</summary>
    public const string TokenPurpose = "Sangam.Scim.BearerToken.v1";

    /// <summary>Pauses between attempts (SGM-217 §4): 1 min, 5 min, 30 min, 2 h, 6 h, 12 h — about a day in all.</summary>
    public static readonly IReadOnlyList<TimeSpan> Backoff = [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(30), TimeSpan.FromHours(2), TimeSpan.FromHours(6), TimeSpan.FromHours(12)];

    /// <summary>How often each application is reconciled.</summary>
    public static readonly TimeSpan ReconcileEvery = TimeSpan.FromHours(24);

    private readonly SangamDbContext _db;
    private readonly IHttpClientFactory _http;
    private readonly IDataProtector _protector;
    private readonly IServiceTokenIssuer? _tokens;
    private readonly IAuditWriter _audit;
    private readonly IClock _clock;
    private readonly ILogger<ScimProvisioner> _logger;
    private readonly IntegrationAlerts _alerts;

    /// <summary>Initialises the provisioner.</summary>
    /// <param name="db">Database.</param>
    /// <param name="http">HTTP clients (<see cref="OutboundHttp.ClientName"/>).</param>
    /// <param name="protection">Data protection, for the bearer tokens.</param>
    /// <param name="audit">Audit writer.</param>
    /// <param name="clock">Clock.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="alerts">Tells the application's owners when provisioning stops working.</param>
    /// <param name="tokens">Signs Sangam's own tokens (the identity server registers it).</param>
    public ScimProvisioner(SangamDbContext db, IHttpClientFactory http, IDataProtectionProvider protection, IAuditWriter audit, IClock clock, ILogger<ScimProvisioner> logger, IntegrationAlerts alerts, IServiceTokenIssuer? tokens = null)
    {
        _alerts = alerts ?? throw new ArgumentNullException(nameof(alerts));
        ArgumentNullException.ThrowIfNull(protection);
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _protector = protection.CreateProtector(TokenPurpose);
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _tokens = tokens;
    }

    /// <summary>Runs every delivery that is due; returns how many succeeded.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<int> DeliverDueAsync(CancellationToken cancellationToken = default)
    {
        DateTimeOffset now = _clock.UtcNow;
        List<ScimDelivery> due = await _db.ScimDeliveries.Where(d => d.Status == "pending" && d.NextAttemptAt <= now).OrderBy(d => d.Id).Take(50).ToListAsync(cancellationToken).ConfigureAwait(false);
        int succeeded = 0;
        foreach (IGrouping<Guid, ScimDelivery> perApp in due.GroupBy(d => d.AppId))
        {
            ScimTarget? target = await _db.ScimTargets.FirstOrDefaultAsync(t => t.AppId == perApp.Key, cancellationToken).ConfigureAwait(false);
            foreach (ScimDelivery delivery in perApp)
            {
                if (target is null || !target.Enabled)
                {
                    Finish(delivery, "dead", "Provisioning is switched off for this application.", null, 0); // i18n-ignore: kept in the delivery log as written
                    continue;
                }

                if (await RunAsync(target, delivery, cancellationToken).ConfigureAwait(false))
                {
                    succeeded++;
                }
            }
        }

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return succeeded;
    }

    /// <summary>Queues everyone the application should know, and everyone it was given, and compares the counts.</summary>
    /// <param name="appId">The application.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A one-line summary for the console.</returns>
    public async Task<string> ReconcileAsync(Guid appId, CancellationToken cancellationToken = default)
    {
        ScimTarget? target = await _db.ScimTargets.FirstOrDefaultAsync(t => t.AppId == appId, cancellationToken).ConfigureAwait(false);
        if (target is null || !target.Enabled)
        {
            return "Provisioning is switched off."; // i18n-ignore: kept in the delivery log as written
        }

        DateTimeOffset now = _clock.UtcNow;
        HashSet<Guid> people = [.. await ShouldBeActiveQuery(appId, now).ToListAsync(cancellationToken).ConfigureAwait(false)];
        int expected = people.Count;
        people.UnionWith(await _db.ScimUserLinks.AsNoTracking().Where(l => l.AppId == appId).Select(l => l.UserId).ToListAsync(cancellationToken).ConfigureAwait(false));
        HashSet<Guid?> waiting = [.. await _db.ScimDeliveries.AsNoTracking().Where(d => d.AppId == appId && d.Status == "pending").Select(d => d.UserId).ToListAsync(cancellationToken).ConfigureAwait(false)];
        int queued = 0;
        foreach (Guid person in people.Where(p => !waiting.Contains(p)))
        {
            _db.ScimDeliveries.Add(new ScimDelivery { AppId = appId, UserId = person, Reason = "reconcile", CreatedAt = now, NextAttemptAt = now });
            queued++;
        }

        // The SCIM server's own count of active users (filter on active; a server without filtering gives its total).
        ScimClient client = Client(target);
        ScimCall count = await client.GetAsync("/Users?filter=" + Uri.EscapeDataString("active eq true") + "&count=0", cancellationToken).ConfigureAwait(false);
        int? remote = count.Ok ? count.Body?["totalResults"]?.GetValue<int>() : null;
        string summary = remote is null
            ? $"{expected} should be active; {queued} queued to bring in step; the server's count could not be read ({count.Line})." // i18n-ignore: kept in the delivery log as written
            : $"{expected} should be active, the server reports {remote}; {queued} queued to bring in step."; // i18n-ignore: kept in the delivery log as written
        target.LastReconciledAt = now;
        target.LastReconcileSummary = summary.Length > 500 ? summary[..500] : summary;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await _audit.WriteAsync(new AuditEntry(AuditActions.ScimReconcile, AuditActorType.System, TargetType: "app", TargetId: appId, Metadata: System.Text.Json.JsonSerializer.Serialize(new { expected, remote, queued })), cancellationToken).ConfigureAwait(false);
        return summary;
    }

    /// <summary>Reconciles every application not reconciled for a day; returns how many were.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<int> ReconcileDueAsync(CancellationToken cancellationToken = default)
    {
        DateTimeOffset cutoff = _clock.UtcNow - ReconcileEvery;
        List<Guid> due = await _db.ScimTargets.AsNoTracking().Where(t => t.Enabled && (t.LastReconciledAt == null || t.LastReconciledAt < cutoff)).Select(t => t.AppId).ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (Guid app in due)
        {
            await ReconcileAsync(app, cancellationToken).ConfigureAwait(false);
        }

        return due.Count;
    }

    /// <summary>Asks the SCIM server for its configuration, to test the address and the token.</summary>
    /// <param name="baseUrl">The SCIM base address.</param>
    /// <param name="authMode"><c>bearer</c> or <c>sangam</c>.</param>
    /// <param name="bearerToken">The bearer token (for <c>bearer</c>).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<ScimCall> TestAsync(string baseUrl, string authMode, string? bearerToken, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        ScimClient client = new(_http.CreateClient(OutboundHttp.ClientName), baseUrl, () => authMode == "sangam" ? SignedToken(baseUrl) : bearerToken ?? string.Empty);
        return client.GetAsync("/ServiceProviderConfig", cancellationToken);
    }

    /// <summary>Protects a bearer token for storage.</summary>
    /// <param name="token">The token.</param>
    public string Protect(string token) => _protector.Protect(token);

    /// <summary>Reads a stored bearer token.</summary>
    /// <param name="stored">The protected token.</param>
    public string Unprotect(string stored) => _protector.Unprotect(stored);

    /// <summary>Who should be provisioned in the application now.</summary>
    /// <param name="appId">The application.</param>
    /// <param name="now">Now.</param>
    internal IQueryable<Guid> ShouldBeActiveQuery(Guid appId, DateTimeOffset now)
        => _db.OrgMemberships.AsNoTracking()
            .Where(m => m.AppId == appId && m.RevokedAt == null && (m.ExpiresAt == null || m.ExpiresAt > now))
            .Where(m => m.User!.Status == UserStatus.Active)
            .Where(m => _db.AppGrants.Any(g => g.AppId == appId && g.UserId == m.UserId && g.RevokedAt == null))
            .Select(m => m.UserId)
            .Distinct();

    private async Task<bool> RunAsync(ScimTarget target, ScimDelivery delivery, CancellationToken cancellationToken)
    {
        delivery.Attempts++;
        List<string> lines = [];
        ScimCall? failure;
        int latency = 0;
        ScimClient client = Client(target);
        try
        {
            failure = delivery.UserId is Guid person
                ? await SyncUserAsync(client, target, person, lines, cancellationToken).ConfigureAwait(false)
                : await SyncGroupNamesAsync(client, target, lines, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            failure = new ScimCall(0, null, "configuration", ex.Message, 0);
        }

        delivery.Summary = Trim(lines.Count == 0 ? "nothing to change" : string.Join(" · ", lines), 1000);
        DateTimeOffset now = _clock.UtcNow;
        if (failure is null)
        {
            Finish(delivery, "done", null, 200, latency);
            target.LastSuccessAt = now;
            target.Status = "ok";
            return true;
        }

        delivery.LastStatusCode = failure.Status == 0 ? null : failure.Status;
        delivery.LatencyMs = failure.LatencyMs;
        delivery.LastError = Trim(failure.Error ?? failure.Line, 1000);
        target.LastFailureAt = now;
        if (delivery.Attempts > Backoff.Count)
        {
            Finish(delivery, "dead", delivery.LastError, delivery.LastStatusCode, failure.LatencyMs);
            if (target.Status != "failing")
            {
                target.Status = "failing";
                await _audit.WriteAsync(new AuditEntry(AuditActions.ScimFailing, AuditActorType.System, TargetType: "app", TargetId: target.AppId, Metadata: System.Text.Json.JsonSerializer.Serialize(new { delivery = delivery.Id, error = delivery.LastError })), cancellationToken).ConfigureAwait(false);
                await _alerts.FailingAsync(target.AppId, "SCIM provisioning (" + target.BaseUrl + ")", "provisioning", delivery.LastError, cancellationToken).ConfigureAwait(false);
            }

            LogGaveUp(target.AppId, delivery.Id, delivery.LastError);
        }
        else
        {
            delivery.NextAttemptAt = now + Backoff[delivery.Attempts - 1];
        }

        return false;
    }

    private async Task<ScimCall?> SyncUserAsync(ScimClient client, ScimTarget target, Guid userId, List<string> lines, CancellationToken cancellationToken)
    {
        DateTimeOffset now = _clock.UtcNow;
        SangamUser? user = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, cancellationToken).ConfigureAwait(false);
        bool active = user is not null && await ShouldBeActiveQuery(target.AppId, now).AnyAsync(id => id == userId, cancellationToken).ConfigureAwait(false);
        ScimUserLink? link = await _db.ScimUserLinks.FirstOrDefaultAsync(l => l.AppId == target.AppId && l.UserId == userId, cancellationToken).ConfigureAwait(false);
        List<ScimGroupMember> inGroups = await _db.ScimGroupMembers.Where(m => m.AppId == target.AppId && m.UserId == userId).ToListAsync(cancellationToken).ConfigureAwait(false);

        if (!active)
        {
            if (link is null)
            {
                return null;
            }

            foreach (ScimGroupMember member in inGroups)
            {
                ScimCall? removed = await RemoveFromGroupAsync(client, target, member, link.RemoteId, lines, cancellationToken).ConfigureAwait(false);
                if (removed is not null)
                {
                    return removed;
                }
            }

            if (target.DeleteOnDeprovision)
            {
                ScimCall deleted = await client.DeleteAsync("/Users/" + Uri.EscapeDataString(link.RemoteId), cancellationToken).ConfigureAwait(false);
                lines.Add(deleted.Line);
                if (!deleted.Ok && deleted.Status != 404)
                {
                    return deleted;
                }

                _db.ScimUserLinks.Remove(link);
            }
            else if (link.Active)
            {
                ScimCall off = await client.PatchAsync("/Users/" + Uri.EscapeDataString(link.RemoteId), [new JsonObject { ["op"] = "replace", ["value"] = new JsonObject { ["active"] = false } }], cancellationToken).ConfigureAwait(false);
                lines.Add(off.Line);
                if (!off.Ok && off.Status != 404)
                {
                    return off;
                }

                link.Active = false;
                link.SyncedAt = now;
            }

            return null;
        }

        // The person as the application may see them: what its scopes and the person's consent allow.
        var memberships = await _db.OrgMemberships.AsNoTracking()
            .Where(m => m.AppId == target.AppId && m.UserId == userId && m.RevokedAt == null && (m.ExpiresAt == null || m.ExpiresAt > now))
            .Select(m => new { m.OrgId, OrgName = m.Org!.Name, m.Role!.Code, RoleName = m.Role.DisplayName })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        string scope = await _db.Consents.AsNoTracking().Where(c => c.AppId == target.AppId && c.UserId == userId && c.RevokedAt == null).OrderByDescending(c => c.GrantedAt).Select(c => c.Scope).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false) ?? string.Empty;
        bool phone = scope.Split(' ').Contains(SangamScopes.Phone) && !string.IsNullOrEmpty(user!.PhoneNumber);
        JsonObject resource = UserResource(user!, phone, memberships.Select(m => m.OrgName).FirstOrDefault());

        if (link is null)
        {
            // Adopt a person the server already has under Sangam's id (an earlier attempt, or a manual import).
            ScimCall found = await client.GetAsync("/Users?filter=" + Uri.EscapeDataString("externalId eq " + ScimClient.Quote(userId.ToString("D"))), cancellationToken).ConfigureAwait(false);
            lines.Add(found.Line);
            if (!found.Ok)
            {
                return found;
            }

            string? existing = found.Body?["Resources"]?.AsArray().FirstOrDefault()?["id"]?.GetValue<string>();
            if (existing is null)
            {
                ScimCall created = await client.PostAsync("/Users", resource, cancellationToken).ConfigureAwait(false);
                lines.Add(created.Line);
                if (!created.Ok)
                {
                    return created;
                }

                existing = created.Body?["id"]?.GetValue<string>();
                if (existing is null)
                {
                    return created with { Error = "The server created the user but did not say its id." }; // i18n-ignore: kept in the delivery log as written
                }
            }
            else
            {
                ScimCall replaced = await ReplaceUserAsync(client, existing, resource, cancellationToken).ConfigureAwait(false);
                lines.Add(replaced.Line);
                if (!replaced.Ok)
                {
                    return replaced;
                }
            }

            link = new ScimUserLink { AppId = target.AppId, UserId = userId, RemoteId = existing, Active = true, SyncedAt = now };
            _db.ScimUserLinks.Add(link);
        }
        else
        {
            ScimCall replaced = await ReplaceUserAsync(client, link.RemoteId, resource, cancellationToken).ConfigureAwait(false);
            lines.Add(replaced.Line);
            if (replaced.Status == 404)
            {
                // Removed on the server: create again and keep the new id.
                ScimCall created = await client.PostAsync("/Users", resource, cancellationToken).ConfigureAwait(false);
                lines.Add(created.Line);
                if (!created.Ok || created.Body?["id"]?.GetValue<string>() is not string id)
                {
                    return created.Ok ? created with { Error = "The server created the user but did not say its id." } : created; // i18n-ignore: kept in the delivery log as written
                }

                link.RemoteId = id;
                _db.ScimGroupMembers.RemoveRange(inGroups);
                inGroups.Clear();
            }
            else if (!replaced.Ok)
            {
                return replaced;
            }

            link.Active = true;
            link.SyncedAt = now;
        }

        // Groups: exactly the ones the person's roles call for.
        Dictionary<string, string> wanted = [];
        foreach (var m in memberships)
        {
            string key = target.GroupMapping == "role_org" ? m.Code + "@" + m.OrgId.ToString("D") : m.Code;
            wanted[key] = target.GroupMapping == "role_org" ? m.RoleName + " — " + m.OrgName : m.RoleName;
        }

        foreach ((string key, string name) in wanted)
        {
            if (inGroups.Any(g => g.GroupKey == key))
            {
                continue;
            }

            (ScimGroupLink? group, ScimCall? problem) = await EnsureGroupAsync(client, target, key, name, lines, cancellationToken).ConfigureAwait(false);
            if (group is null)
            {
                return problem;
            }

            ScimCall added = await client.PatchAsync("/Groups/" + Uri.EscapeDataString(group.RemoteId), [new JsonObject { ["op"] = "add", ["path"] = "members", ["value"] = new JsonArray(new JsonObject { ["value"] = link.RemoteId }) }], cancellationToken).ConfigureAwait(false);
            lines.Add(added.Line);
            if (!added.Ok)
            {
                return added;
            }

            _db.ScimGroupMembers.Add(new ScimGroupMember { AppId = target.AppId, GroupKey = key, UserId = userId });
        }

        foreach (ScimGroupMember member in inGroups.Where(g => !wanted.ContainsKey(g.GroupKey)).ToList())
        {
            ScimCall? removed = await RemoveFromGroupAsync(client, target, member, link.RemoteId, lines, cancellationToken).ConfigureAwait(false);
            if (removed is not null)
            {
                return removed;
            }
        }

        return null;
    }

    private async Task<ScimCall?> SyncGroupNamesAsync(ScimClient client, ScimTarget target, List<string> lines, CancellationToken cancellationToken)
    {
        List<ScimGroupLink> groups = await _db.ScimGroupLinks.Where(g => g.AppId == target.AppId).ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (ScimGroupLink group in groups)
        {
            string code = group.GroupKey.Split('@')[0];
            Guid? org = group.GroupKey.Contains('@', StringComparison.Ordinal) && Guid.TryParse(group.GroupKey.Split('@')[1], out Guid o) ? o : null;
            string? role = await _db.Roles.AsNoTracking().Where(r => r.AppId == target.AppId && r.Code == code && r.RetiredAt == null && (r.OrgId == null || r.OrgId == org)).OrderByDescending(r => r.OrgId != null).Select(r => r.DisplayName).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            if (role is null)
            {
                continue;
            }

            string name = org is Guid orgId ? role + " — " + await _db.Organisations.AsNoTracking().Where(x => x.Id == orgId).Select(x => x.Name).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false) : role;
            if (name == group.DisplayName)
            {
                continue;
            }

            ScimCall renamed = await client.PatchAsync("/Groups/" + Uri.EscapeDataString(group.RemoteId), [new JsonObject { ["op"] = "replace", ["path"] = "displayName", ["value"] = name }], cancellationToken).ConfigureAwait(false);
            lines.Add(renamed.Line);
            if (!renamed.Ok)
            {
                return renamed;
            }

            group.DisplayName = name;
        }

        return null;
    }

    private async Task<(ScimGroupLink? Group, ScimCall? Problem)> EnsureGroupAsync(ScimClient client, ScimTarget target, string key, string name, List<string> lines, CancellationToken cancellationToken)
    {
        ScimGroupLink? group = await _db.ScimGroupLinks.FirstOrDefaultAsync(g => g.AppId == target.AppId && g.GroupKey == key, cancellationToken).ConfigureAwait(false)
            ?? _db.ScimGroupLinks.Local.FirstOrDefault(g => g.AppId == target.AppId && g.GroupKey == key);
        if (group is not null)
        {
            return (group, null);
        }

        JsonObject body = new()
        {
            ["schemas"] = new JsonArray(ScimClient.GroupSchema),
            ["displayName"] = name,
            ["externalId"] = key,
            ["members"] = new JsonArray(),
        };
        ScimCall created = await client.PostAsync("/Groups", body, cancellationToken).ConfigureAwait(false);
        lines.Add(created.Line);
        if (!created.Ok || created.Body?["id"]?.GetValue<string>() is not string id)
        {
            return (null, created.Ok ? created with { Error = "The server created the group but did not say its id." } : created); // i18n-ignore: kept in the delivery log as written
        }

        group = new ScimGroupLink { AppId = target.AppId, GroupKey = key, RemoteId = id, DisplayName = name };
        _db.ScimGroupLinks.Add(group);
        return (group, null);
    }

    private async Task<ScimCall?> RemoveFromGroupAsync(ScimClient client, ScimTarget target, ScimGroupMember member, string remoteUser, List<string> lines, CancellationToken cancellationToken)
    {
        ScimGroupLink? group = await _db.ScimGroupLinks.AsNoTracking().FirstOrDefaultAsync(g => g.AppId == target.AppId && g.GroupKey == member.GroupKey, cancellationToken).ConfigureAwait(false);
        if (group is not null)
        {
            ScimCall removed = await client.PatchAsync("/Groups/" + Uri.EscapeDataString(group.RemoteId), [new JsonObject { ["op"] = "remove", ["path"] = "members[value eq " + ScimClient.Quote(remoteUser) + "]" }], cancellationToken).ConfigureAwait(false);
            lines.Add(removed.Line);
            if (!removed.Ok && removed.Status != 404)
            {
                return removed;
            }
        }

        _db.ScimGroupMembers.Remove(member);
        return null;
    }

    private static Task<ScimCall> ReplaceUserAsync(ScimClient client, string remoteId, JsonObject resource, CancellationToken cancellationToken)
    {
        JsonObject value = resource.DeepClone().AsObject();
        value.Remove("schemas");
        return client.PatchAsync("/Users/" + Uri.EscapeDataString(remoteId), [new JsonObject { ["op"] = "replace", ["value"] = value }], cancellationToken);
    }

    /// <summary>The SCIM user Sangam sends: ids, name, e-mail, the mobile if consented, active, the organisation.</summary>
    /// <param name="user">The person.</param>
    /// <param name="phone">Whether the mobile is released.</param>
    /// <param name="organisation">Their first organisation in the application.</param>
    internal static JsonObject UserResource(SangamUser user, bool phone, string? organisation)
    {
        JsonObject resource = new()
        {
            ["schemas"] = new JsonArray(ScimClient.UserSchema, ScimClient.EnterpriseSchema),
            ["externalId"] = user.Id.ToString("D"),
            ["userName"] = user.Email,
            ["name"] = new JsonObject { ["givenName"] = user.FirstName, ["familyName"] = user.LastName, ["formatted"] = (user.FirstName + " " + user.LastName).Trim() },
            ["displayName"] = (user.FirstName + " " + user.LastName).Trim(),
            ["emails"] = new JsonArray(new JsonObject { ["value"] = user.Email, ["type"] = "work", ["primary"] = true }),
            ["active"] = true,
        };
        if (phone)
        {
            resource["phoneNumbers"] = new JsonArray(new JsonObject { ["value"] = user.PhoneNumber, ["type"] = "mobile" });
        }

        if (organisation is not null)
        {
            resource[ScimClient.EnterpriseSchema] = new JsonObject { ["organization"] = organisation };
        }

        return resource;
    }

    private ScimClient Client(ScimTarget target)
        => new(_http.CreateClient(OutboundHttp.ClientName), target.BaseUrl, () => target.AuthMode == "sangam"
            ? SignedToken(target.BaseUrl)
            : target.ProtectedToken is { Length: > 0 } stored ? _protector.Unprotect(stored) : throw new InvalidOperationException("No bearer token is set for this SCIM server."));

    private string SignedToken(string audience)
        => (_tokens ?? throw new InvalidOperationException("Sangam-signed tokens are issued by the identity server only.")).Issue(audience, TimeSpan.FromMinutes(5));

    private static void Finish(ScimDelivery delivery, string status, string? error, int? code, int latency)
    {
        delivery.Status = status;
        delivery.CompletedAt = DateTimeOffset.UtcNow;
        delivery.LastError = error is null ? null : Trim(error, 1000);
        delivery.LastStatusCode = code;
        delivery.LatencyMs = latency;
    }

    private static string Trim(string value, int max) => value.Length > max ? value[..max] : value;

    [LoggerMessage(EventId = 2101, Level = LogLevel.Warning, Message = "SCIM delivery {Delivery} for app {AppId} given up: {Error}")]
    private partial void LogGaveUp(Guid appId, long delivery, string? error);
}
