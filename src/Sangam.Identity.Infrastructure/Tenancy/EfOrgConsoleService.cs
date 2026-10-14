using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Sangam.Identity.Application.Abstractions;
using Sangam.Identity.Application.Customisation;
using Sangam.Identity.Application.Partners;
using Sangam.Identity.Application.Tenancy;
using Sangam.Identity.Domain;
using Sangam.Identity.Domain.Entities;
using Sangam.Identity.Domain.Enums;
using Sangam.Identity.Infrastructure.Persistence;
using Sangam.Identity.Infrastructure.Services;
using Sangam.Shared;

namespace Sangam.Identity.Infrastructure.Tenancy;

/// <summary><see cref="IOrgConsoleService"/> over the identity database (rc.12, G1 and G2).</summary>
public sealed class EfOrgConsoleService : IOrgConsoleService
{
    /// <summary>How far ahead the overview looks for roles ending.</summary>
    public static readonly TimeSpan EndingWindow = TimeSpan.FromDays(14);

    private const int ReasonLength = 300;

    private readonly SangamDbContext _db;
    private readonly StaffRights _rights;
    private readonly IManagementService _management;
    private readonly IMessageTemplates _templates;
    private readonly IEmailSender _email;
    private readonly IAuditWriter _audit;
    private readonly IClock _clock;

    /// <summary>Initialises the service.</summary>
    /// <param name="db">Database.</param>
    /// <param name="rights">Who may manage staff where.</param>
    /// <param name="management">The tenancy rules, through which places end.</param>
    /// <param name="templates">Message templates.</param>
    /// <param name="email">E-mail sender.</param>
    /// <param name="audit">Audit writer.</param>
    /// <param name="clock">Clock.</param>
    public EfOrgConsoleService(SangamDbContext db, StaffRights rights, IManagementService management, IMessageTemplates templates, IEmailSender email, IAuditWriter audit, IClock clock)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _rights = rights ?? throw new ArgumentNullException(nameof(rights));
        _management = management ?? throw new ArgumentNullException(nameof(management));
        _templates = templates ?? throw new ArgumentNullException(nameof(templates));
        _email = email ?? throw new ArgumentNullException(nameof(email));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<OrgScope>> ScopesAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        List<OrgScope> scopes = [];

        // An application admin administers every customer of the application.
        var appRoots = await (
            from a in _db.AppAdmins.AsNoTracking()
            where a.UserId == userId && a.RevokedAt == null
            join app in _db.Apps.AsNoTracking() on a.AppId equals app.Id
            join o in _db.Organisations.AsNoTracking() on app.Id equals o.RegisteredViaAppId
            where o.ParentOrgId == null && o.DeletedAt == null && o.Status != OrganisationStatus.Closed
            select new { App = app, Org = o }).ToListAsync(cancellationToken).ConfigureAwait(false);
        scopes.AddRange(appRoots.Select(x => new OrgScope(x.App.Id, x.App.DisplayName, x.App.BrandColour, x.App.Glyph, x.Org.Id, x.Org.Name, x.Org.Path, true, true)));

        var rights = await (
            from r in _db.OrgAdmins.AsNoTracking()
            where r.UserId == userId && r.RevokedAt == null
            join app in _db.Apps.AsNoTracking() on r.AppId equals app.Id
            join o in _db.Organisations.AsNoTracking() on r.OrgId equals o.Id
            where o.DeletedAt == null && o.Status != OrganisationStatus.Closed
            select new { App = app, Org = o, r.Right }).ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var r in rights.OrderBy(r => r.Org.Depth).ThenBy(r => r.Right))
        {
            bool reviewer = r.Right == OrgAdminRight.EmergencyReviewer;
            int at = scopes.FindIndex(s => s.AppId == r.App.Id && s.OrgId == r.Org.Id);
            if (at >= 0)
            {
                scopes[at] = reviewer ? scopes[at] with { Reviewer = true } : scopes[at] with { Admin = true };
                continue;
            }

            // A right below a tree the person already administers adds nothing.
            if (scopes.Any(s => s.Admin && s.AppId == r.App.Id && r.Org.Path.StartsWith(s.Path, StringComparison.Ordinal)))
            {
                continue;
            }

            // A reviewer who is not an admin there sees the tree for its emergency access only.
            scopes.Add(new OrgScope(r.App.Id, r.App.DisplayName, r.App.BrandColour, r.App.Glyph, r.Org.Id, r.Org.Name, r.Org.Path, r.Org.ParentOrgId == null, false, Admin: !reviewer, Reviewer: reviewer));
        }

        return [.. scopes.OrderBy(s => s.OrgName, StringComparer.CurrentCulture).ThenBy(s => s.AppName, StringComparer.CurrentCulture)];
    }

    /// <inheritdoc />
    public async Task<OrgOverview?> OverviewAsync(Guid userId, Guid appId, Guid orgId, CancellationToken cancellationToken = default)
    {
        Organisation? org = await ManagedAsync(userId, appId, orgId, cancellationToken).ConfigureAwait(false);
        if (org is null)
        {
            return null;
        }

        DateTimeOffset now = _clock.UtcNow;
        DateTimeOffset soon = now + EndingWindow;
        IQueryable<OrgMembership> live = Live(appId, org.Path, now);
        int staff = await live.Select(m => m.UserId).Distinct().CountAsync(cancellationToken).ConfigureAwait(false);
        var ending = await live
            .Where(m => m.ExpiresAt != null && m.ExpiresAt <= soon)
            .Select(m => new { m.UserId, m.OrgId, OrgName = m.Org!.Name, m.User!.FirstName, m.User.LastName, Role = m.Role == null ? null : m.Role.Code, m.ExpiresAt })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var waiting = await (
            from p in _db.PendingMemberships.AsNoTracking()
            where p.AppId == appId && p.AppliedAt == null && p.CancelledAt == null
            join o in _db.Organisations.AsNoTracking() on p.OrgId equals o.Id
            where o.Path.StartsWith(org.Path)
            join i in _db.StaffInvites.AsNoTracking() on p.InviteId equals i.Id into invites
            from i in invites.DefaultIfEmpty()
            join u in _db.Users.AsNoTracking() on p.UserId equals u.Id into users
            from u in users.DefaultIfEmpty()
            where i == null || (i.CancelledAt == null && (i.AcceptedAt != null || i.ExpiresAt > now))
            select new
            {
                p.Id,
                p.OrgId,
                OrgName = o.Name,
                p.UserId,
                p.CreatedAt,
                p.StartsOn,
                Name = u != null ? u.FirstName + " " + u.LastName : i != null ? i.FirstName + " " + i.LastName : string.Empty,
                NotJoined = p.UserId == null,
            }).ToListAsync(cancellationToken).ConfigureAwait(false);

        // rc.12 (M12): emergency access in the last seven days, and every use still waiting for its review.
        DateTimeOffset week = now.AddDays(-7);
        var emergencies = await _db.EmergencyAccesses.AsNoTracking()
            .Where(e => e.AppId == appId && (e.StartedAt >= week || e.ReviewedAt == null))
            .Join(_db.Organisations.Where(o => o.Path.StartsWith(org.Path)), e => e.OrgId, o => o.Id, (e, o) => new { e.Id, e.OrgId, OrgName = o.Name, e.UserId, e.CaseRef, e.StartedAt, e.ReviewedAt })
            .Join(_db.Users, x => x.UserId, u => u.Id, (x, u) => new { x.Id, x.OrgId, x.OrgName, x.UserId, x.CaseRef, x.StartedAt, x.ReviewedAt, u.FirstName, u.LastName })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        string appName = await _db.Apps.AsNoTracking().Where(a => a.Id == appId).Select(a => a.DisplayName).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false) ?? string.Empty;
        List<OrgDecision> decisions =
        [
            .. emergencies.Where(e => e.ReviewedAt == null).Select(e => new OrgDecision(OrgDecisionKind.EmergencyUse, (e.FirstName + " " + e.LastName).Trim(), e.OrgName, appName + " · " + e.CaseRef, e.StartedAt, e.UserId, e.OrgId, e.Id)),
            .. ending.GroupBy(e => (e.UserId, e.OrgId)).Select(g => new OrgDecision(
                OrgDecisionKind.RoleEnding,
                (g.First().FirstName + " " + g.First().LastName).Trim(),
                g.First().OrgName,
                appName + (g.Any(e => e.Role is not null) ? " · " + string.Join(", ", g.Where(e => e.Role is not null).Select(e => e.Role).Distinct()) : string.Empty),
                g.Min(e => e.ExpiresAt!.Value),
                g.Key.UserId,
                g.Key.OrgId)),
            .. waiting.Where(w => w.NotJoined).Select(w => new OrgDecision(OrgDecisionKind.NotJoined, w.Name.Trim(), w.OrgName, appName, w.CreatedAt, null, w.OrgId, w.Id)),
        ];

        bool? kept = await _db.Organisations.AsNoTracking().Where(o => o.Id == OrganisationPath.CustomerId(org.Path)).Select(o => o.RolesKeptInSangam).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return new OrgOverview(
            staff,
            ending.Select(e => (e.UserId, e.OrgId)).Distinct().Count(),
            waiting.Count,
            emergencies.Count(e => e.StartedAt >= week),
            [.. decisions.OrderBy(d => d.Kind == OrgDecisionKind.EmergencyUse ? 0 : 1).ThenBy(d => d.Kind == OrgDecisionKind.NotJoined ? d.When + StaffInvite.Lifetime : d.When).Take(20)],
            kept != false,
            org.EmergencyAccess);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<OrgNode>?> TreeAsync(Guid userId, Guid appId, Guid orgId, CancellationToken cancellationToken = default)
    {
        Organisation? org = await ManagedAsync(userId, appId, orgId, cancellationToken).ConfigureAwait(false);
        if (org is null)
        {
            return null;
        }

        DateTimeOffset now = _clock.UtcNow;
        List<Organisation> orgs = await _db.Organisations.AsNoTracking()
            .Where(o => o.RegisteredViaAppId == appId && o.DeletedAt == null && o.Path.StartsWith(org.Path))
            .OrderBy(o => o.Path)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        Dictionary<Guid, int> people = await Live(appId, org.Path, now)
            .GroupBy(m => m.OrgId)
            .Select(g => new { g.Key, People = g.Select(m => m.UserId).Distinct().Count() })
            .ToDictionaryAsync(x => x.Key, x => x.People, cancellationToken).ConfigureAwait(false);

        // Depth-first, children by name, so the list reads as the tree.
        ILookup<Guid?, Organisation> children = orgs.ToLookup(o => o.Id == org.Id ? null : o.ParentOrgId);
        List<OrgNode> nodes = [];
        void Walk(Organisation node, int depth)
        {
            nodes.Add(new OrgNode(node.Id, node.Name, node.Id == org.Id ? null : node.ParentOrgId, depth, people.GetValueOrDefault(node.Id), node.EmergencyAccess));
            foreach (Organisation child in children[node.Id].OrderBy(c => c.Name, StringComparer.CurrentCulture))
            {
                Walk(child, depth + 1);
            }
        }

        Walk(org, 0);
        return nodes;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<OrgPerson>?> PeopleAsync(Guid userId, Guid appId, Guid orgId, CancellationToken cancellationToken = default)
    {
        Organisation? org = await ManagedAsync(userId, appId, orgId, cancellationToken).ConfigureAwait(false);
        if (org is null)
        {
            return null;
        }

        DateTimeOffset now = _clock.UtcNow;
        var rows = await _db.OrgMemberships.AsNoTracking()
            .Where(m => m.AppId == appId && m.OrgId == orgId && m.RevokedAt == null && (m.ExpiresAt == null || m.ExpiresAt > now))
            .Select(m => new { m.UserId, m.User!.FirstName, m.User.LastName, m.User.Email, Role = m.Role == null ? null : m.Role.Code, m.GrantedAt, m.ExpiresAt })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var rights = await _db.OrgAdmins.AsNoTracking()
            .Where(a => a.AppId == appId && a.OrgId == orgId && a.RevokedAt == null)
            .Select(a => new { a.UserId, a.Right })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        List<OrgPerson> people =
        [
            .. rows.GroupBy(r => r.UserId).Select(g => new OrgPerson(
                g.Key,
                (g.First().FirstName + " " + g.First().LastName).Trim(),
                g.First().Email ?? string.Empty,
                [.. g.Where(r => r.Role is not null).Select(r => r.Role!).Distinct().Order(StringComparer.Ordinal)],
                g.Min(r => r.GrantedAt),
                g.Any(r => r.ExpiresAt is null) ? null : g.Max(r => r.ExpiresAt),
                rights.Any(a => a.UserId == g.Key && a.Right == OrgAdminRight.Admin),
                rights.Any(a => a.UserId == g.Key && a.Right == OrgAdminRight.EmergencyReviewer)))
            .OrderBy(p => p.Name, StringComparer.CurrentCulture),
        ];

        // rc.8 (ASVS V8.3.5): reading a list of people is recorded.
        await _audit.WriteAsync(
            new AuditEntry(AuditActions.AppPeopleView, AuditActorType.Admin, userId, appId, "organisation", orgId,
                Metadata: JsonSerializer.Serialize(new Dictionary<string, object> { ["list"] = "org_console_people", ["rows"] = people.Count, ["searched"] = false })),
            cancellationToken).ConfigureAwait(false);
        return people;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<OrgRoleChoice>> RolesAsync(Guid userId, Guid appId, Guid orgId, CancellationToken cancellationToken = default)
    {
        Organisation? org = await ManagedAsync(userId, appId, orgId, cancellationToken).ConfigureAwait(false);
        if (org is null)
        {
            return [];
        }

        Guid customerId = OrganisationPath.CustomerId(org.Path);
        bool? kept = await _db.Organisations.AsNoTracking().Where(o => o.Id == customerId).Select(o => o.RolesKeptInSangam).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (kept == false)
        {
            return [];
        }

        List<Role> roles = await _db.Roles.AsNoTracking()
            .Where(r => r.AppId == appId && r.RetiredAt == null && (r.OrgId == null || r.OrgId == orgId))
            .OrderByDescending(r => r.OrgId != null)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return
        [
            .. roles.GroupBy(r => r.Code).Select(g => g.First())
                .Select(r => new OrgRoleChoice(r.Code, string.IsNullOrWhiteSpace(r.DisplayName) ? r.Code : r.DisplayName))
                .OrderBy(r => r.Name, StringComparer.CurrentCulture),
        ];
    }

    /// <inheritdoc />
    public async Task<PartnerResult> EndAsync(Guid userId, Guid appId, Guid orgId, Guid personId, DateOnly? endsOn, string reason, CancellationToken cancellationToken = default)
    {
        if (await ManagedAsync(userId, appId, orgId, cancellationToken).ConfigureAwait(false) is null)
        {
            return PartnerResult.Refused("You are not an admin there.");
        }

        if (Reason(reason) is not string why)
        {
            return PartnerResult.Refused("Give a reason; the person reads it in their Activity.");
        }

        return await EndPlaceAsync(userId, appId, orgId, personId, endsOn, why, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<OrgLeaver>> FindAsync(Guid userId, string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        string term = text.Trim();
        if (term.Length < 2)
        {
            return [];
        }

        DateTimeOffset now = _clock.UtcNow;
        List<OrgScope> scopes = [.. (await ScopesAsync(userId, cancellationToken).ConfigureAwait(false)).Where(s => s.Admin)];
        string like = term.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal) + "%";
        List<Guid> appIds = [.. scopes.Select(s => s.AppId).Distinct()];
        var rows = await _db.OrgMemberships.AsNoTracking()
            .Where(m => appIds.Contains(m.AppId) && m.RevokedAt == null && (m.ExpiresAt == null || m.ExpiresAt > now)
                && (EF.Functions.ILike(m.User!.FirstName, like) || EF.Functions.ILike(m.User.LastName, like) || EF.Functions.ILike(m.User.FirstName + " " + m.User.LastName, like) || EF.Functions.ILike(m.User.Email!, like)))
            .Select(m => new { m.UserId, m.User!.FirstName, m.User.LastName, m.User.Email, m.AppId, AppName = m.App!.DisplayName, m.OrgId, OrgName = m.Org!.Name, m.Org.Path, Role = m.Role == null ? null : m.Role.Code })
            .Take(500)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        List<OrgLeaver> found =
        [
            .. rows.Where(r => scopes.Any(s => s.AppId == r.AppId && r.Path.StartsWith(s.Path, StringComparison.Ordinal)))
                .GroupBy(r => r.UserId)
                .Select(g => new OrgLeaver(
                    g.Key,
                    (g.First().FirstName + " " + g.First().LastName).Trim(),
                    LogRedaction.MaskEmail(g.First().Email ?? string.Empty),
                    [.. g.GroupBy(r => (r.AppId, r.OrgId)).Select(p => new OrgLeaverPlace(p.Key.AppId, p.First().AppName, p.Key.OrgId, p.First().OrgName, [.. p.Where(r => r.Role is not null).Select(r => r.Role!).Distinct()]))]))
                .OrderBy(l => l.Name, StringComparer.CurrentCulture)
                .Take(10),
        ];
        await _audit.WriteAsync(
            new AuditEntry(AuditActions.AppPeopleView, AuditActorType.Admin, userId, TargetType: "user", TargetId: userId,
                Metadata: JsonSerializer.Serialize(new Dictionary<string, object> { ["list"] = "org_console_find", ["rows"] = found.Count, ["searched"] = true })),
            cancellationToken).ConfigureAwait(false);
        return found;
    }

    /// <inheritdoc />
    public async Task<int> LeaverAsync(Guid userId, Guid personId, DateOnly? lastDay, string reason, CancellationToken cancellationToken = default)
    {
        if (Reason(reason) is not string why)
        {
            return 0;
        }

        DateTimeOffset now = _clock.UtcNow;
        List<OrgScope> scopes = [.. (await ScopesAsync(userId, cancellationToken).ConfigureAwait(false)).Where(s => s.Admin)];
        List<Guid> appIds = [.. scopes.Select(s => s.AppId).Distinct()];
        var places = await _db.OrgMemberships.AsNoTracking()
            .Where(m => m.UserId == personId && appIds.Contains(m.AppId) && m.RevokedAt == null && (m.ExpiresAt == null || m.ExpiresAt > now))
            .Select(m => new { m.AppId, m.OrgId, m.Org!.Path })
            .Distinct()
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        int ended = 0;
        foreach (var place in places.Where(p => scopes.Any(s => s.AppId == p.AppId && p.Path.StartsWith(s.Path, StringComparison.Ordinal))))
        {
            if ((await EndPlaceAsync(userId, place.AppId, place.OrgId, personId, lastDay, why, cancellationToken).ConfigureAwait(false)).Succeeded)
            {
                ended++;
            }
        }

        await _audit.WriteAsync(
            new AuditEntry(AuditActions.StaffLeaver, AuditActorType.Admin, userId, TargetType: "user", TargetId: personId,
                Metadata: JsonSerializer.Serialize(new Dictionary<string, object?> { ["places"] = ended, ["last_day"] = lastDay?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), ["reason"] = why })),
            cancellationToken).ConfigureAwait(false);
        return ended;
    }

    /// <inheritdoc />
    public async Task<PartnerResult> GrantAsync(Guid userId, Guid appId, Guid orgId, Guid personId, bool reviewer, CancellationToken cancellationToken = default)
    {
        Organisation? org = await ManagedAsync(userId, appId, orgId, cancellationToken).ConfigureAwait(false);
        if (org is null)
        {
            return PartnerResult.Refused("You are not an admin there.");
        }

        if (!(await _rights.MayAddAsync(appId, userId, cancellationToken).ConfigureAwait(false)).May)
        {
            return PartnerResult.Refused("Only an admin verified with DigiLocker can make others admins.");
        }

        DateTimeOffset now = _clock.UtcNow;
        bool hasPlace = await Live(appId, org.Path, now).AnyAsync(m => m.UserId == personId, cancellationToken).ConfigureAwait(false);
        if (!hasPlace)
        {
            return PartnerResult.Refused("Add the person to this organisation first; an admin must work here.");
        }

        OrgAdminRight right = reviewer ? OrgAdminRight.EmergencyReviewer : OrgAdminRight.Admin;
        if (await _db.OrgAdmins.AnyAsync(a => a.AppId == appId && a.OrgId == orgId && a.UserId == personId && a.Right == right && a.RevokedAt == null, cancellationToken).ConfigureAwait(false))
        {
            return PartnerResult.Ok(reviewer ? "They already review emergency access here." : "They are already an admin here.");
        }

        _db.OrgAdmins.Add(new OrgAdmin { Id = Guid.NewGuid(), AppId = appId, OrgId = orgId, UserId = personId, Right = right, GrantedByUserId = userId, GrantedAt = now });
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await AuditRightAsync(AuditActions.OrgAdminGrant, userId, appId, orgId, personId, right, cancellationToken).ConfigureAwait(false);
        return PartnerResult.Ok(reviewer ? "They now review emergency access here." : "They are now an admin here.");
    }

    /// <inheritdoc />
    public async Task<PartnerResult> RevokeAsync(Guid userId, Guid appId, Guid orgId, Guid personId, bool reviewer, CancellationToken cancellationToken = default)
    {
        if (await ManagedAsync(userId, appId, orgId, cancellationToken).ConfigureAwait(false) is null)
        {
            return PartnerResult.Refused("You are not an admin there.");
        }

        OrgAdminRight right = reviewer ? OrgAdminRight.EmergencyReviewer : OrgAdminRight.Admin;
        OrgAdmin? held = await _db.OrgAdmins.FirstOrDefaultAsync(a => a.AppId == appId && a.OrgId == orgId && a.UserId == personId && a.Right == right && a.RevokedAt == null, cancellationToken).ConfigureAwait(false);
        if (held is null)
        {
            return PartnerResult.Refused("They do not hold that right here.");
        }

        held.RevokedAt = _clock.UtcNow;
        held.RevokedByUserId = userId;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await AuditRightAsync(AuditActions.OrgAdminRevoke, userId, appId, orgId, personId, right, cancellationToken).ConfigureAwait(false);
        return PartnerResult.Ok(reviewer ? "They no longer review emergency access here." : "They are no longer an admin here.");
    }

    /// <inheritdoc />
    public async Task<PartnerResult> SetRolesKeptAsync(Guid userId, Guid appId, Guid orgId, bool kept, CancellationToken cancellationToken = default)
    {
        Organisation? org = await ManagedAsync(userId, appId, orgId, cancellationToken).ConfigureAwait(false);
        if (org is null || org.ParentOrgId is not null)
        {
            return PartnerResult.Refused("Only a customer's own admins choose where its roles are kept.");
        }

        Organisation tracked = await _db.Organisations.SingleAsync(o => o.Id == orgId, cancellationToken).ConfigureAwait(false);
        tracked.RolesKeptInSangam = kept;
        tracked.UpdatedAt = _clock.UtcNow;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await _audit.WriteAsync(
            new AuditEntry(AuditActions.OrgRolesKept, AuditActorType.Admin, userId, appId, "organisation", orgId, Metadata: JsonSerializer.Serialize(new { kept_in_sangam = kept })),
            cancellationToken).ConfigureAwait(false);
        return PartnerResult.Ok(kept
            ? "Roles are kept in Sangam: admins choose them when adding people."
            : "Roles are kept in the application: people are added as staff, and roles they already hold stay until changed.");
    }

    /// <inheritdoc />
    public async Task<PartnerResult> SetEmergencyAccessAsync(Guid userId, Guid appId, Guid orgId, bool on, CancellationToken cancellationToken = default)
    {
        if (await ManagedAsync(userId, appId, orgId, cancellationToken).ConfigureAwait(false) is null)
        {
            return PartnerResult.Refused("You are not an admin there.");
        }

        Organisation tracked = await _db.Organisations.SingleAsync(o => o.Id == orgId, cancellationToken).ConfigureAwait(false);
        tracked.EmergencyAccess = on;
        tracked.UpdatedAt = _clock.UtcNow;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await _audit.WriteAsync(
            new AuditEntry(AuditActions.OrgEmergencyAccess, AuditActorType.Admin, userId, appId, "organisation", orgId, Metadata: JsonSerializer.Serialize(new { on })),
            cancellationToken).ConfigureAwait(false);
        return PartnerResult.Ok(on ? "Emergency access is on here." : "Emergency access is off here.");
    }

    /// <inheritdoc />
    public async Task<PartnerResult> CloseCustomerAsync(Guid userId, Guid appId, Guid orgId, string reason, CancellationToken cancellationToken = default)
    {
        if (!await _db.AppAdmins.AnyAsync(a => a.AppId == appId && a.UserId == userId && a.RevokedAt == null, cancellationToken).ConfigureAwait(false))
        {
            return PartnerResult.Refused("Only the application's administrators can close a customer.");
        }

        Organisation? customer = await _db.Organisations.FirstOrDefaultAsync(o => o.Id == orgId && o.RegisteredViaAppId == appId && o.ParentOrgId == null && o.DeletedAt == null, cancellationToken).ConfigureAwait(false);
        if (customer is null || customer.Status == OrganisationStatus.Closed)
        {
            return PartnerResult.Refused("That customer does not exist, or is already closed.");
        }

        if (Reason(reason) is not string why)
        {
            return PartnerResult.Refused("Give a reason for closing the customer.");
        }

        DateTimeOffset now = _clock.UtcNow;
        string path = customer.Path;
        var places = await _db.OrgMemberships.AsNoTracking()
            .Where(m => m.AppId == appId && m.RevokedAt == null && m.Org!.Path.StartsWith(path))
            .Select(m => new { m.OrgId, m.UserId })
            .Distinct()
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        ManagementActor actor = ManagementActor.AppAdmin(userId) with { Reason = why };
        foreach (var place in places)
        {
            await _management.RevokeMembershipAsync(appId, place.OrgId, place.UserId, actor, cancellationToken).ConfigureAwait(false);
        }

        List<Organisation> tree = await _db.Organisations.Where(o => o.RegisteredViaAppId == appId && o.Path.StartsWith(path) && o.DeletedAt == null).ToListAsync(cancellationToken).ConfigureAwait(false);
        List<Guid> treeIds = [.. tree.Select(o => o.Id)];
        foreach (OrgAdmin right in await _db.OrgAdmins.Where(a => a.AppId == appId && treeIds.Contains(a.OrgId) && a.RevokedAt == null).ToListAsync(cancellationToken).ConfigureAwait(false))
        {
            right.RevokedAt = now;
            right.RevokedByUserId = userId;
        }

        foreach (PendingMembership waiting in await _db.PendingMemberships.Where(p => p.AppId == appId && treeIds.Contains(p.OrgId) && p.AppliedAt == null && p.CancelledAt == null).ToListAsync(cancellationToken).ConfigureAwait(false))
        {
            waiting.CancelledAt = now;
        }

        foreach (Organisation o in tree)
        {
            o.Status = OrganisationStatus.Closed;
            o.ClosedAt = now;
            o.UpdatedAt = now;
        }

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await _audit.WriteAsync(
            new AuditEntry(AuditActions.OrgClose, AuditActorType.Admin, userId, appId, "organisation", orgId,
                Metadata: JsonSerializer.Serialize(new Dictionary<string, object> { ["reason"] = why, ["places"] = places.Count, ["organisations"] = tree.Count })),
            cancellationToken).ConfigureAwait(false);

        string application = await _db.Apps.AsNoTracking().Where(a => a.Id == appId).Select(a => a.DisplayName).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false) ?? string.Empty;
        List<Guid> people = [.. places.Select(p => p.UserId).Distinct()];
        foreach (SangamUser person in await _db.Users.AsNoTracking().Where(u => people.Contains(u.Id) && u.Email != null).ToListAsync(cancellationToken).ConfigureAwait(false))
        {
            EmailMessage message = await _templates.EmailAsync(
                MessageTemplateKinds.OrganisationClosed,
                person.Locale,
                appId,
                null,
                new Dictionary<string, string>(StringComparer.Ordinal) { ["name"] = person.FirstName, ["organisation"] = customer.Name, ["application"] = application },
                person.Email!,
                person.FirstName,
                cancellationToken).ConfigureAwait(false);
            await _email.SendAsync(message, cancellationToken).ConfigureAwait(false);
        }

        return PartnerResult.Ok($"{customer.Name} is closed. {people.Count} people were told; their accounts stay theirs.");
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>The organisation, when the person administers it (and it is open).</summary>
    private async Task<Organisation?> ManagedAsync(Guid userId, Guid appId, Guid orgId, CancellationToken cancellationToken)
    {
        Organisation? org = await _rights.OrganisationAsync(appId, orgId, cancellationToken).ConfigureAwait(false);
        return org is not null && org.Status != OrganisationStatus.Closed && await _rights.ManagesAsync(appId, org, userId, cancellationToken).ConfigureAwait(false) ? org : null;
    }

    private IQueryable<OrgMembership> Live(Guid appId, string path, DateTimeOffset now)
        => _db.OrgMemberships.AsNoTracking().Where(m => m.AppId == appId && m.RevokedAt == null && (m.ExpiresAt == null || m.ExpiresAt > now) && m.Org!.Path.StartsWith(path));

    private async Task<PartnerResult> EndPlaceAsync(Guid userId, Guid appId, Guid orgId, Guid personId, DateOnly? endsOn, string why, CancellationToken cancellationToken)
    {
        ManagementActor actor = ManagementActor.AppAdmin(userId) with { Reason = why };
        if (endsOn is not DateOnly day || day <= StaffCodes.IndiaToday(_clock.UtcNow).AddDays(-1))
        {
            ManagementResult<MembershipDto> removed = await _management.RevokeMembershipAsync(appId, orgId, personId, actor, cancellationToken).ConfigureAwait(false);
            return removed.Status == ManagementStatus.Ok ? PartnerResult.Ok("Ended.") : PartnerResult.Refused(removed.Message ?? "Nothing to end.");
        }

        List<string?> current = await _db.OrgMemberships.AsNoTracking()
            .Where(m => m.AppId == appId && m.OrgId == orgId && m.UserId == personId && m.RevokedAt == null)
            .Select(m => m.Role == null ? null : m.Role.Code)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        if (current.Count == 0)
        {
            return PartnerResult.Refused("That person no longer has a place here.");
        }

        // The place ends at the end of the chosen day, India time.
        DateTimeOffset until = new(day.ToDateTime(new TimeOnly(23, 59, 59)), IndiaTime.Offset);
        ManagementResult<MembershipDto> shortened = await _management
            .UpsertMembershipAsync(appId, orgId, personId, new MembershipUpsert(null, false, until, [.. current.Where(c => c is not null).Select(c => c!)]), actor, cancellationToken).ConfigureAwait(false);
        return shortened.Status == ManagementStatus.Ok ? PartnerResult.Ok("It ends on that day.") : PartnerResult.Refused(shortened.Message ?? "Nothing changed.");
    }

    private static string? Reason(string? reason)
    {
        string? why = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        return why is { Length: > ReasonLength } ? why[..ReasonLength] : why;
    }

    private Task AuditRightAsync(string action, Guid userId, Guid appId, Guid orgId, Guid personId, OrgAdminRight right, CancellationToken cancellationToken)
        => _audit.WriteAsync(
            new AuditEntry(action, AuditActorType.Admin, userId, appId, "user", personId,
                Metadata: JsonSerializer.Serialize(new Dictionary<string, object> { ["org"] = orgId, ["right"] = right == OrgAdminRight.Admin ? "admin" : "emergency_reviewer" })),
            cancellationToken);
}
