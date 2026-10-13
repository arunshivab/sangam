using Microsoft.EntityFrameworkCore;
using OpenIddict.EntityFrameworkCore.Models;
using Sangam.Identity.Application.Admin;
using Sangam.Identity.Domain.Entities;
using Sangam.Identity.Domain.Enums;

namespace Sangam.Identity.Infrastructure.Admin;

/// <summary>rc.10: the console's paged users list, its work queue and the owner's overview.</summary>
public sealed partial class EfAdminService
{
    /// <summary>How soon a grievance deadline counts as due on the overview.</summary>
    private static readonly TimeSpan DueSoon = TimeSpan.FromDays(2);

    /// <inheritdoc />
    public async Task<AdminUserPage> ListUsersAsync(Guid operatorUserId, AdminUserQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        int size = Math.Clamp(query.PageSize, 1, 100);
        int page = Math.Max(1, query.Page);
        if (await RequireAsync(operatorUserId, PlatformRole.Viewer, cancellationToken).ConfigureAwait(false) is not null)
        {
            return new AdminUserPage([], 0, page, size);
        }

        IQueryable<SangamUser> users = _db.Users.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(query.Text))
        {
            string term = query.Text.Trim();
            string digits = new([.. term.Where(char.IsDigit)]);
            users = users.Where(u =>
                EF.Functions.ILike(u.Email!, $"%{term}%")
                || EF.Functions.ILike(u.FirstName + " " + u.LastName, $"%{term}%")
                || (digits.Length >= 4 && u.PhoneNumber != null && EF.Functions.ILike(u.PhoneNumber, $"%{digits}%")));
        }

        if (query.Status is UserStatus status)
        {
            users = users.Where(u => u.Status == status);
        }

        if (!string.IsNullOrWhiteSpace(query.Organisation))
        {
            string org = query.Organisation.Trim();
            DateTimeOffset now = _clock.UtcNow;
            users = users.Where(u => _db.OrgMemberships.Any(m =>
                m.UserId == u.Id && m.RevokedAt == null && (m.ExpiresAt == null || m.ExpiresAt > now) && EF.Functions.ILike(m.Org!.Name, $"%{org}%")));
        }

        int total = await users.CountAsync(cancellationToken).ConfigureAwait(false);
        int pages = Math.Max(1, (total + size - 1) / size);
        page = Math.Min(page, pages);
        var rows = await users
            .OrderByDescending(u => u.CreatedAt)
            .ThenBy(u => u.Id)
            .Skip((page - 1) * size)
            .Take(size)
            .Select(u => new
            {
                u.Id,
                u.FirstName,
                u.LastName,
                u.Email,
                u.PhoneNumber,
                u.Status,
                u.EmailConfirmed,
                u.TwoFactorEnabled,
                u.CreatedAt,
                u.PurgeAfter,
                u.HoldPlacedAt,
                IsOperator = _db.PlatformOperators.Any(o => o.UserId == u.Id && o.RevokedAt == null),
            })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        List<AdminUserRow> list =
        [
            .. rows.Select(r => new AdminUserRow(
                r.Id,
                (r.FirstName + " " + r.LastName).Trim(),
                r.Email ?? string.Empty,
                r.PhoneNumber,
                r.Status,
                r.EmailConfirmed,
                r.TwoFactorEnabled,
                r.IsOperator,
                r.CreatedAt,
                r.PurgeAfter,
                r.HoldPlacedAt is not null)),
        ];
        return new AdminUserPage(list, total, page, size);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<WorkItem>> WorkQueueAsync(Guid operatorUserId, CancellationToken cancellationToken = default)
    {
        // Read the rank without the refusal audit: every page's sidebar counts the queue, and a viewer simply sees less.
        PlatformRole? role = await GetRoleAsync(operatorUserId, cancellationToken).ConfigureAwait(false);
        if (role is null)
        {
            return [];
        }

        DateTimeOffset now = _clock.UtcNow;
        List<WorkItem> items = [];
        if (role >= PlatformRole.Support)
        {
            var resets = await _db.MfaResetRequests.AsNoTracking()
                .Where(r => r.CancelledAt == null && r.AppliedAt == null && r.ReviewStatus != MfaResetReview.Refused)
                .Join(_db.Users, r => r.UserId, u => u.Id, (r, u) => new { r.Id, r.UserId, r.RequestedAt, r.EffectiveAt, r.Privileged, r.ReviewStatus, u.FirstName, u.LastName })
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            foreach (var r in resets)
            {
                string name = (r.FirstName + " " + r.LastName).Trim();
                items.Add(r.ReviewStatus == MfaResetReview.Waiting
                    ? new WorkItem(WorkKind.Recovery, r.Id, name, r.RequestedAt, null, r.Privileged, "/recoveries", false)
                    : new WorkItem(WorkKind.TwoStepReset, r.Id, name, r.RequestedAt, r.EffectiveAt, r.Privileged, $"/users/{r.UserId:D}", false));
            }
        }

        List<Grievance> open = await _db.Grievances.AsNoTracking().Where(g => g.ClosedAt == null).ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (Grievance g in open)
        {
            DateTimeOffset due = g.AcknowledgedAt is null ? g.AcknowledgeBy : g.ResolveBy;
            items.Add(new WorkItem(WorkKind.Grievance, g.Id, g.Reference + " · " + g.ComplainantName, g.ReceivedAt, due, false, $"/grievances/{g.Id:D}", due < now));
        }

        // A recovery waiting for a decision first, the longest waiting first: someone is locked out until an operator
        // decides. Then everything with a clock, the soonest first.
        return [.. items.OrderBy(i => i.Kind == WorkKind.Recovery ? 0 : 1).ThenBy(i => i.Due ?? i.Since).ThenBy(i => i.Since)];
    }

    /// <inheritdoc />
    public async Task<AdminOverview?> OverviewAsync(Guid operatorUserId, CancellationToken cancellationToken = default)
    {
        if (await GetRoleAsync(operatorUserId, cancellationToken).ConfigureAwait(false) != PlatformRole.Owner)
        {
            return null;
        }

        DateTimeOffset now = _clock.UtcNow;
        int people = await _db.Users.CountAsync(u => u.Status == UserStatus.Active || u.Status == UserStatus.Suspended, cancellationToken).ConfigureAwait(false);
        int newThisWeek = await _db.Users.CountAsync(u => u.CreatedAt > now.AddDays(-7), cancellationToken).ConfigureAwait(false);
        int signIns = await _db.UserSessions.CountAsync(s => s.CreatedAt > now.AddDays(-1), cancellationToken).ConfigureAwait(false);
        int applications = await _db.Apps.CountAsync(a => a.Status == AppStatus.Active, cancellationToken).ConfigureAwait(false);

        List<AttentionItem> attention = [];
        IReadOnlyList<WorkItem> queue = await WorkQueueAsync(operatorUserId, cancellationToken).ConfigureAwait(false);
        List<WorkItem> due = [.. queue.Where(i => i.Kind == WorkKind.Grievance && i.Due is DateTimeOffset d && d - now < DueSoon)];
        if (due.Count > 0)
        {
            attention.Add(new AttentionItem(AttentionKind.GrievancesDue, string.Empty, due.Count, due.Min(i => i.Due), "/queue?kind=grievance"));
        }

        int recoveries = queue.Count(i => i.Kind == WorkKind.Recovery);
        if (recoveries > 0)
        {
            attention.Add(new AttentionItem(AttentionKind.RecoveriesWaiting, string.Empty, recoveries, null, "/recoveries"));
        }

        List<MonitoringAlert> alerts = await _db.MonitoringAlerts.AsNoTracking().Where(a => a.ResolvedAt == null).OrderBy(a => a.OpenedAt).Take(5).ToListAsync(cancellationToken).ConfigureAwait(false);
        attention.AddRange(alerts.Select(a => new AttentionItem(AttentionKind.Alert, a.Summary, 0, null, "/monitoring")));

        foreach (AdminAppRow app in await ListAppsAsync(operatorUserId, cancellationToken).ConfigureAwait(false))
        {
            if (!app.IsPlatform && app.Status == AppStatus.Active && app.PartnerOwners == 0)
            {
                attention.Add(new AttentionItem(AttentionKind.AppWithoutOwner, app.DisplayName, 0, null, "/apps"));
            }

            if (app.SecretIsOld(now))
            {
                attention.Add(new AttentionItem(AttentionKind.SecretOld, app.DisplayName, 0, null, "/apps"));
            }
        }

        var trail = await _db.AuditEvents.AsNoTracking()
            .Where(e => e.ActorType == AuditActorType.Admin || e.ActorType == AuditActorType.System)
            .OrderByDescending(e => e.Id)
            .Take(12)
            .Select(e => new { e.OccurredAt, e.ActorUserId, e.ActorType, e.Action, e.TargetType })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        List<Guid> actors = [.. trail.Where(t => t.ActorUserId != null).Select(t => t.ActorUserId!.Value).Distinct()];
        Dictionary<Guid, string> names = await _db.Users.AsNoTracking().Where(u => actors.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => (u.FirstName + " " + u.LastName).Trim(), cancellationToken).ConfigureAwait(false);

        return new AdminOverview(
            people,
            newThisWeek,
            signIns,
            applications,
            [.. attention.OrderBy(a => a.Due ?? DateTimeOffset.MaxValue)],
            [.. trail.Select(t => new TrailRow(
                t.OccurredAt,
                t.ActorUserId is Guid id && names.TryGetValue(id, out string? name) ? name : "Sangam",
                t.Action,
                t.TargetType,
                Failed(t.Action)))]);
    }

    /// <summary>Whether an audit action records a refusal or a failure.</summary>
    private static bool Failed(string action)
        => action.EndsWith(".fail", StringComparison.Ordinal)
        || action.EndsWith(".failing", StringComparison.Ordinal)
        || action.EndsWith(".refused", StringComparison.Ordinal)
        || action.EndsWith(".denied", StringComparison.Ordinal)
        || action.EndsWith(".refuse", StringComparison.Ordinal);

    /// <summary>For each application, whether it is a confidential client (signs in with a secret), by client id.</summary>
    private async Task<Dictionary<string, bool>> ConfidentialByClientIdAsync(CancellationToken cancellationToken)
    {
        var clients = await _db.Set<OpenIddictEntityFrameworkCoreApplication>().AsNoTracking()
            .Where(a => a.ClientId != null)
            .Select(a => new { a.ClientId, a.ClientType })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return clients.ToDictionary(c => c.ClientId!, c => string.Equals(c.ClientType, OpenIddict.Abstractions.OpenIddictConstants.ClientTypes.Confidential, StringComparison.Ordinal), StringComparer.Ordinal);
    }
}
