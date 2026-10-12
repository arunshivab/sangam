using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Sangam.Identity.Application.Abstractions;
using Sangam.Identity.Application.Portal;
using Sangam.Identity.Application.Tenancy;
using Sangam.Identity.Domain;
using Sangam.Identity.Domain.Entities;
using Sangam.Identity.Domain.Enums;
using Sangam.Identity.Infrastructure.Accounts;
using Sangam.Identity.Infrastructure.Persistence;

namespace Sangam.Identity.Infrastructure.Portal;

/// <summary><see cref="IPortalRolesService"/> over the identity database (rc.10, P4).</summary>
public sealed class EfPortalRolesService : IPortalRolesService
{
    /// <summary>The system role every application has for the people who administer an organisation in it.</summary>
    public const string OrganisationAdminRole = "org_admin";

    private readonly SangamDbContext _db;
    private readonly IManagementService _management;
    private readonly SecurityNotices _notices;
    private readonly IAuditWriter _audit;
    private readonly IClock _clock;

    /// <summary>Initialises the service.</summary>
    /// <param name="db">Database.</param>
    /// <param name="management">The tenancy rules, through which a role is ended (and each application told).</param>
    /// <param name="notices">E-mail notices.</param>
    /// <param name="audit">Audit writer.</param>
    /// <param name="clock">Clock.</param>
    public EfPortalRolesService(SangamDbContext db, IManagementService management, SecurityNotices notices, IAuditWriter audit, IClock clock)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _management = management ?? throw new ArgumentNullException(nameof(management));
        _notices = notices ?? throw new ArgumentNullException(nameof(notices));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PortalOrganisation>> GetAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        List<Held> held = await HeldAsync(userId, null, cancellationToken).ConfigureAwait(false);
        if (held.Count == 0)
        {
            return [];
        }

        HashSet<Guid> ancestorIds = [.. held.SelectMany(h => Ancestors(h.OrgPath))];
        Dictionary<Guid, string> ancestorNames = await _db.Organisations.AsNoTracking()
            .Where(o => ancestorIds.Contains(o.Id))
            .ToDictionaryAsync(o => o.Id, o => o.Name, cancellationToken).ConfigureAwait(false);
        List<Guid> granters = [.. held.Where(h => h.GrantedByUserId != null).Select(h => h.GrantedByUserId!.Value).Distinct()];
        Dictionary<Guid, string> granterNames = await _db.Users.AsNoTracking()
            .Where(u => granters.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => (u.FirstName + " " + u.LastName).Trim(), cancellationToken).ConfigureAwait(false);
        HashSet<(Guid OrgId, Guid AppId)> onlyAdmin = await OnlyAdminAsync(userId, held, cancellationToken).ConfigureAwait(false);

        return
        [
            .. held.GroupBy(h => h.OrgId)
                .OrderByDescending(g => g.Max(h => h.GrantedAt))
                .Select(g =>
                {
                    Held first = g.First();
                    List<string> within = [.. Ancestors(first.OrgPath).Select(id => ancestorNames.GetValueOrDefault(id)).Where(n => n is not null).Select(n => n!)];
                    return new PortalOrganisation(
                        g.Key,
                        first.OrgName,
                        within.Count == 0 ? null : string.Join(" / ", within),
                        [
                            .. g.OrderBy(h => h.AppName, StringComparer.CurrentCulture).Select(h => new PortalRole(
                                h.AppId,
                                h.AppName,
                                string.IsNullOrWhiteSpace(h.RoleName) ? h.RoleCode : h.RoleName,
                                h.GrantedByUserId is Guid by ? granterNames.GetValueOrDefault(by) : null,
                                h.GrantedAt,
                                h.ExpiresAt,
                                h.AppliesToDescendants,
                                onlyAdmin.Contains((h.OrgId, h.AppId)))),
                        ]);
                }),
        ];
    }

    /// <inheritdoc />
    public async Task<LeaveResult> LeaveAsync(Guid userId, Guid orgId, string? ipAddress, CancellationToken cancellationToken = default)
    {
        List<Held> held = await HeldAsync(userId, orgId, cancellationToken).ConfigureAwait(false);
        if (held.Count == 0)
        {
            return new LeaveResult(false, [], []);
        }

        HashSet<(Guid OrgId, Guid AppId)> onlyAdmin = await OnlyAdminAsync(userId, held, cancellationToken).ConfigureAwait(false);
        ManagementActor self = ManagementActor.Self(userId);
        List<Held> ended = [];
        List<string> kept = [];
        foreach (Held h in held)
        {
            if (onlyAdmin.Contains((h.OrgId, h.AppId)))
            {
                kept.Add(h.AppName);
                continue;
            }

            // The same path a partner uses: audited, and the application is told (membership revoked; the person
            // deactivated there when it was their last role).
            if ((await _management.RevokeMembershipAsync(h.AppId, h.OrgId, userId, self, cancellationToken).ConfigureAwait(false)).Status == ManagementStatus.Ok)
            {
                ended.Add(h);
            }
        }

        string orgName = held[0].OrgName;
        await _audit.WriteAsync(
            new AuditEntry(AuditActions.UserOrganisationLeave, AuditActorType.User, userId, TargetType: "user", TargetId: userId, IpAddress: ipAddress,
                Metadata: JsonSerializer.Serialize(new Dictionary<string, object>
                {
                    ["org"] = orgId,
                    ["organisation"] = orgName,
                    ["ended"] = ended.Select(h => h.AppName).Distinct().ToList(),
                    ["kept"] = kept.Distinct().ToList(),
                })),
            cancellationToken).ConfigureAwait(false);

        if (ended.Count > 0)
        {
            await TellAsync(userId, orgId, orgName, ended, cancellationToken).ConfigureAwait(false);
        }

        return new LeaveResult(ended.Count > 0, [.. ended.Select(h => h.AppName).Distinct()], [.. kept.Distinct()]);
    }

    /// <summary>The person, and each application's organisation admins at that organisation, are told by e-mail.</summary>
    private async Task TellAsync(Guid userId, Guid orgId, string orgName, List<Held> ended, CancellationToken cancellationToken)
    {
        SangamUser? person = await _db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == userId, cancellationToken).ConfigureAwait(false);
        if (person is null)
        {
            return;
        }

        await _notices.SendAsync(
            MessageTemplateKinds.OrganisationLeft,
            person.Id,
            person.Email,
            person.FirstName,
            person.Locale,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["organisation"] = orgName,
                ["roles"] = string.Join("\n", ended.Select(h => "  - " + h.AppName + ": " + RoleText(h))),
            },
            cancellationToken).ConfigureAwait(false);

        string member = (person.FirstName + " " + person.LastName).Trim();
        DateTimeOffset now = _clock.UtcNow;
        foreach (IGrouping<Guid, Held> app in ended.GroupBy(h => h.AppId))
        {
            var admins = await _db.OrgMemberships.AsNoTracking()
                .Where(m => m.AppId == app.Key && m.OrgId == orgId && m.UserId != userId && m.RevokedAt == null && (m.ExpiresAt == null || m.ExpiresAt > now) && m.Role!.Code == OrganisationAdminRole)
                .Select(m => new { m.UserId, m.User!.Email, m.User.FirstName, m.User.Locale })
                .Distinct()
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            foreach (var admin in admins)
            {
                await _notices.SendAsync(
                    MessageTemplateKinds.OrganisationMemberLeft,
                    admin.UserId,
                    admin.Email,
                    admin.FirstName,
                    admin.Locale,
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["member"] = member,
                        ["organisation"] = orgName,
                        ["application"] = app.First().AppName,
                        ["roles"] = string.Join("\n", app.Select(h => "  - " + RoleText(h))),
                    },
                    cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static string RoleText(Held h) => string.IsNullOrWhiteSpace(h.RoleName) ? h.RoleCode : h.RoleName;

    /// <summary>The person's live roles, at one organisation or all.</summary>
    private async Task<List<Held>> HeldAsync(Guid userId, Guid? orgId, CancellationToken cancellationToken)
    {
        DateTimeOffset now = _clock.UtcNow;
        IQueryable<OrgMembership> query = _db.OrgMemberships.AsNoTracking()
            .Where(m => m.UserId == userId && m.RevokedAt == null && (m.ExpiresAt == null || m.ExpiresAt > now) && m.Org!.DeletedAt == null);
        if (orgId is Guid only)
        {
            query = query.Where(m => m.OrgId == only);
        }

        return await query
            .Select(m => new Held(m.OrgId, m.Org!.Name, m.Org.Path, m.AppId, m.App!.DisplayName, m.Role!.Code, m.Role.DisplayName, m.GrantedByUserId, m.GrantedAt, m.ExpiresAt, m.AppliesToDescendants))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Where the person is the only organisation admin of an application at an organisation: leaving would leave nobody
    /// there who can give or end roles for that application.
    /// </summary>
    private async Task<HashSet<(Guid OrgId, Guid AppId)>> OnlyAdminAsync(Guid userId, List<Held> held, CancellationToken cancellationToken)
    {
        List<Held> admin = [.. held.Where(h => h.RoleCode == OrganisationAdminRole)];
        HashSet<(Guid, Guid)> only = [];
        DateTimeOffset now = _clock.UtcNow;
        foreach (Held h in admin)
        {
            bool another = await _db.OrgMemberships.AnyAsync(
                m => m.AppId == h.AppId && m.OrgId == h.OrgId && m.UserId != userId && m.RevokedAt == null && (m.ExpiresAt == null || m.ExpiresAt > now) && m.Role!.Code == OrganisationAdminRole,
                cancellationToken).ConfigureAwait(false);
            if (!another)
            {
                only.Add((h.OrgId, h.AppId));
            }
        }

        return only;
    }

    /// <summary>The ids of the organisations above the one at <paramref name="path"/>, outermost first.</summary>
    private static IEnumerable<Guid> Ancestors(string path)
    {
        string[] parts = path.Split(OrganisationPath.Separator, StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < parts.Length - 1; i++)
        {
            if (Guid.TryParse(parts[i], out Guid id))
            {
                yield return id;
            }
        }
    }

    private sealed record Held(
        Guid OrgId,
        string OrgName,
        string OrgPath,
        Guid AppId,
        string AppName,
        string RoleCode,
        string RoleName,
        Guid? GrantedByUserId,
        DateTimeOffset GrantedAt,
        DateTimeOffset? ExpiresAt,
        bool AppliesToDescendants);
}
