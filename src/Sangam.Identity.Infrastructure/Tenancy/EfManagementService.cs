using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Sangam.Identity.Application.Abstractions;
using Sangam.Identity.Application.Tenancy;
using Sangam.Identity.Domain;
using Sangam.Identity.Domain.Entities;
using Sangam.Identity.Domain.Enums;
using Sangam.Identity.Infrastructure.Persistence;
using Sangam.Identity.Infrastructure.Provisioning;

namespace Sangam.Identity.Infrastructure.Tenancy;

/// <summary><see cref="IManagementService"/>: the app-scoped writes behind the management API.</summary>
public sealed partial class EfManagementService : IManagementService
{
    /// <summary>The management API's answer for a person it may not add (rc.5): unknown, or not a user of the application.</summary>
    public const string NotAUserOfThisApplication = "That person does not use this application. Add them as staff instead (POST /orgs/{orgId}/staff, with the admin's identity); they join with the admin's code or their own join code.";

    /// <summary>rc.12: the most roles one person may hold in one organisation.</summary>
    private const int MaxRolesPerOrganisation = 10;

    private readonly SangamDbContext _db;
    private readonly IClock _clock;
    private readonly IAuditWriter _audit;

    /// <summary>Initialises the service.</summary>
    /// <param name="db">Database.</param>
    /// <param name="clock">Clock.</param>
    /// <param name="audit">Audit writer.</param>
    public EfManagementService(SangamDbContext db, IClock clock, IAuditWriter audit)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RoleDto>> ListRolesAsync(Guid appId, CancellationToken cancellationToken = default)
    {
        List<Role> roles = await _db.Roles.AsNoTracking().Where(r => r.AppId == appId).OrderBy(r => r.OrgId).ThenBy(r => r.Code).ToListAsync(cancellationToken).ConfigureAwait(false);
        return [.. roles.Select(ToDto)];
    }

    /// <inheritdoc />
    public async Task<ManagementResult<RoleDto>> UpsertRoleAsync(Guid appId, string code, RoleUpsert input, ManagementActor actor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(code);
        ArgumentNullException.ThrowIfNull(input);

        if (!RoleCodeRegex().IsMatch(code))
        {
            return ManagementResult.Invalid<RoleDto>("Role codes are 2-50 characters of lowercase letters, digits and underscores, starting with a letter.");
        }

        if (string.IsNullOrWhiteSpace(input.DisplayName))
        {
            return ManagementResult.Invalid<RoleDto>("A display name is required.");
        }

        // R7 (ASVS V5.1.3): limits the columns hold, checked here, so a long value is a 400 with the reason and never a
        // database error.
        if (input.DisplayName.Length > 200 || input.Description?.Length > 500)
        {
            return ManagementResult.Invalid<RoleDto>("A display name is at most 200 characters, and a description at most 500.");
        }

        if (input.Permissions is null || input.Permissions.Count > 100 || input.Permissions.Any(p => string.IsNullOrWhiteSpace(p) || p.Length > 100))
        {
            return ManagementResult.Invalid<RoleDto>("Permissions are a list of at most 100, each 1 to 100 characters.");
        }

        if (input.OrgId is Guid scopeOrg && !await _db.Organisations.AnyAsync(o => o.Id == scopeOrg, cancellationToken).ConfigureAwait(false))
        {
            return ManagementResult.NotFound<RoleDto>("The organisation to scope the role to does not exist.");
        }

        Role? role = await _db.Roles.FirstOrDefaultAsync(r => r.AppId == appId && r.Code == code && r.OrgId == input.OrgId, cancellationToken).ConfigureAwait(false);
        DateTimeOffset now = _clock.UtcNow;
        string permissions = JsonSerializer.Serialize(input.Permissions.Distinct(StringComparer.Ordinal).OrderBy(p => p, StringComparer.Ordinal));

        if (role is null)
        {
            role = new Role { Id = Guid.NewGuid(), AppId = appId, OrgId = input.OrgId, Code = code, CreatedAt = now };
            _db.Roles.Add(role);
        }
        else if (role.IsSystem && role.OrgId != input.OrgId)
        {
            return ManagementResult.Invalid<RoleDto>("The system role cannot be re-scoped.");
        }

        role.DisplayName = input.DisplayName.Trim();
        role.Description = input.Description?.Trim();
        role.Permissions = permissions;
        role.RetiredAt = null;
        await AppEventLog.AddAsync(_db, AppEventTypes.RoleChanged, appId, null, input.OrgId, new Dictionary<string, object?> { ["role"] = code, ["change"] = "saved" }, now, cancellationToken).ConfigureAwait(false);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await _audit.WriteAsync(
            new AuditEntry(AuditActions.RoleUpsert, actor.Type, actor.UserId, ActorAppId: appId, TargetType: "role", TargetId: role.Id, Metadata: $"{{\"code\":\"{code}\"}}"),
            cancellationToken).ConfigureAwait(false);
        return ManagementResult.Ok<RoleDto>(ToDto(role));
    }

    /// <inheritdoc />
    public async Task<ManagementResult<RoleDto>> RetireRoleAsync(Guid appId, string code, ManagementActor actor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(code);
        Role? role = await _db.Roles.FirstOrDefaultAsync(r => r.AppId == appId && r.Code == code && r.OrgId == null && r.RetiredAt == null, cancellationToken).ConfigureAwait(false);
        if (role is null)
        {
            return ManagementResult.NotFound<RoleDto>("No live app-wide role with that code.");
        }

        if (role.IsSystem)
        {
            return ManagementResult.Invalid<RoleDto>("The system role cannot be retired.");
        }

        role.RetiredAt = _clock.UtcNow;
        await AppEventLog.AddAsync(_db, AppEventTypes.RoleChanged, appId, null, null, new Dictionary<string, object?> { ["role"] = code, ["change"] = "retired" }, role.RetiredAt.Value, cancellationToken).ConfigureAwait(false);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await _audit.WriteAsync(
            new AuditEntry(AuditActions.RoleRetire, actor.Type, actor.UserId, ActorAppId: appId, TargetType: "role", TargetId: role.Id, Metadata: $"{{\"code\":\"{code}\"}}"),
            cancellationToken).ConfigureAwait(false);
        return ManagementResult.Ok<RoleDto>(ToDto(role));
    }

    /// <inheritdoc />
    public async Task<OrganisationDto?> GetOrganisationAsync(Guid appId, Guid orgId, CancellationToken cancellationToken = default)
    {
        Organisation? org = await _db.Organisations.AsNoTracking().FirstOrDefaultAsync(o => o.Id == orgId, cancellationToken).ConfigureAwait(false);
        if (org is null || !await CanSeeAsync(appId, org, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return ToDto(org);
    }

    /// <inheritdoc />
    public async Task<ManagementResult<OrganisationDto>> UpsertOrganisationAsync(Guid appId, Guid orgId, OrganisationUpsert input, ManagementActor actor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(input);
        if (string.IsNullOrWhiteSpace(input.Name))
        {
            return ManagementResult.Invalid<OrganisationDto>("A name is required.");
        }

        if (input.Name.Length > 200)
        {
            return ManagementResult.Invalid<OrganisationDto>("A name is at most 200 characters.");
        }

        if (input.Metadata is not null && (input.Metadata.Length > 8192 || !IsJsonObject(input.Metadata)))
        {
            return ManagementResult.Invalid<OrganisationDto>("Metadata must be a JSON object of at most 8 KB.");
        }

        DateTimeOffset now = _clock.UtcNow;
        Organisation? org = await _db.Organisations.FirstOrDefaultAsync(o => o.Id == orgId, cancellationToken).ConfigureAwait(false);

        if (org is not null)
        {
            if (org.RegisteredViaAppId != appId)
            {
                return ManagementResult.NotFound<OrganisationDto>("The organisation was not registered through this app.");
            }

            org.Name = input.Name.Trim();
            org.Metadata = input.Metadata ?? org.Metadata;
            org.UpdatedAt = now;
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await _audit.WriteAsync(new AuditEntry(AuditActions.OrgUpdate, actor.Type, actor.UserId, ActorAppId: appId, TargetType: "organisation", TargetId: org.Id), cancellationToken).ConfigureAwait(false);
            return ManagementResult.Ok<OrganisationDto>(ToDto(org));
        }

        OrgType? type = await _db.OrgTypes.AsNoTracking().FirstOrDefaultAsync(t => t.Code == input.Type, cancellationToken).ConfigureAwait(false);
        if (type is null)
        {
            return ManagementResult.NotFound<OrganisationDto>("Unknown organisation type.");
        }

        Organisation? parent = null;
        if (input.ParentId is Guid parentId)
        {
            parent = await _db.Organisations.AsNoTracking().FirstOrDefaultAsync(o => o.Id == parentId, cancellationToken).ConfigureAwait(false);
            if (parent is null || !await CanSeeAsync(appId, parent, cancellationToken).ConfigureAwait(false))
            {
                return ManagementResult.NotFound<OrganisationDto>("The parent organisation does not exist.");
            }

            OrgType? parentType = await _db.OrgTypes.AsNoTracking().FirstOrDefaultAsync(t => t.Code == parent.OrgTypeCode, cancellationToken).ConfigureAwait(false);
            if (parentType is not null && !parentType.CanHaveChildren)
            {
                return ManagementResult.Invalid<OrganisationDto>($"A {parentType.DisplayName} cannot have child organisations.");
            }
        }
        else if (!type.CanBeRoot)
        {
            return ManagementResult.Invalid<OrganisationDto>($"A {type.DisplayName} cannot be a root organisation; supply a parent.");
        }

        org = new Organisation
        {
            Id = orgId,
            Name = input.Name.Trim(),
            OrgTypeCode = type.Code,
            ParentOrgId = parent?.Id,
            Depth = parent is null ? 0 : parent.Depth + 1,
            RegisteredViaAppId = appId,
            Metadata = input.Metadata ?? "{}",
            CreatedAt = now,
            UpdatedAt = now,
        };
        org.Path = parent is null ? OrganisationPath.ForRoot(org.Id) : OrganisationPath.ForChild(parent.Path, org.Id);
        _db.Organisations.Add(org);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await _audit.WriteAsync(new AuditEntry(AuditActions.OrgCreate, actor.Type, actor.UserId, ActorAppId: appId, TargetType: "organisation", TargetId: org.Id), cancellationToken).ConfigureAwait(false);
        return ManagementResult.Ok<OrganisationDto>(ToDto(org));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<MembershipDto>> ListMembersAsync(Guid appId, Guid orgId, CancellationToken cancellationToken = default)
    {
        var rows = await _db.OrgMemberships.AsNoTracking()
            .Where(m => m.AppId == appId && m.OrgId == orgId && m.RevokedAt == null && (m.ExpiresAt == null || m.ExpiresAt > DateTimeOffset.UtcNow))
            .OrderBy(m => m.GrantedAt)
            .Select(m => new { m.UserId, m.OrgId, Role = m.Role == null ? null : m.Role.Code, m.GrantedAt, m.ExpiresAt })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return [.. rows.GroupBy(r => r.UserId).Select(g => Dto(g.Key, orgId, g.Select(r => r.Role), g.Min(r => r.GrantedAt), g.Max(r => r.ExpiresAt)))];
    }

    /// <inheritdoc />
    public async Task<ManagementResult<MembershipDto>> UpsertMembershipAsync(Guid appId, Guid orgId, Guid userId, MembershipUpsert input, ManagementActor actor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(input);

        Organisation? org = await _db.Organisations.AsNoTracking().FirstOrDefaultAsync(o => o.Id == orgId, cancellationToken).ConfigureAwait(false);
        if (org is null || !await CanSeeAsync(appId, org, cancellationToken).ConfigureAwait(false))
        {
            return ManagementResult.NotFound<MembershipDto>("The organisation does not exist.");
        }

        if (org.Status != OrganisationStatus.Active)
        {
            return ManagementResult.Invalid<MembershipDto>("The organisation is not active.");
        }

        // rc.12 (decision of 14 Oct 2026): a membership is in its own organisation only.
        if (input.AppliesToDescendants)
        {
            return ManagementResult.Invalid<MembershipDto>("A role no longer flows down to the organisations below. Add the person to each organisation they work in.");
        }

        bool userExists = await _db.Users.AnyAsync(u => u.Id == userId && u.Status == UserStatus.Active, cancellationToken).ConfigureAwait(false);

        // rc.5 (ASVS V4.2.1, consent first): through the API an application adds only people who already use it —
        // who signed in to it and allowed it. Anyone else is added as staff with an activation code (rc.12), and they are
        // linked when they accept. One answer for an unknown id and a person who does not use the application, so
        // the API never tells which ids are Sangam users.
        if (actor.Type == AuditActorType.Api
            && (!userExists || !await _db.AppGrants.AnyAsync(g => g.UserId == userId && g.AppId == appId && g.RevokedAt == null, cancellationToken).ConfigureAwait(false)))
        {
            return ManagementResult.NotFound<MembershipDto>(NotAUserOfThisApplication);
        }

        if (!userExists)
        {
            return ManagementResult.NotFound<MembershipDto>("The user does not exist or is not active.");
        }

        List<string> codes = [.. input.RoleCodes.Select(c => c.Trim()).Where(c => c.Length > 0).Distinct(StringComparer.Ordinal)];
        if (codes.Count > MaxRolesPerOrganisation)
        {
            return ManagementResult.Invalid<MembershipDto>("At most 10 roles in one organisation.");
        }

        if (codes.Count > 0 && !await RolesKeptInSangamAsync(org, cancellationToken).ConfigureAwait(false))
        {
            return ManagementResult.Invalid<MembershipDto>("This customer keeps its roles in the application, not in Sangam. Add the person without roles.");
        }

        // Org-scoped roles for this org win over app-wide ones of the same code.
        // (Order on the predicate, not on OrgId: PostgreSQL sorts NULLS FIRST on DESC.)
        List<Role> live = await _db.Roles.AsNoTracking()
            .Where(r => r.AppId == appId && codes.Contains(r.Code) && r.RetiredAt == null && (r.OrgId == null || r.OrgId == orgId))
            .OrderByDescending(r => r.OrgId != null)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        Dictionary<string, Role> roles = [];
        foreach (Role r in live)
        {
            roles.TryAdd(r.Code, r);
        }

        if (roles.Count != codes.Count)
        {
            return ManagementResult.NotFound<MembershipDto>(codes.Count == 1 ? "No live role with that code for this app." : "No live role with one of those codes for this app.");
        }

        DateTimeOffset now = _clock.UtcNow;
        if (input.ExpiresAt is DateTimeOffset until && (until <= now || until > now.AddYears(5)))
        {
            return ManagementResult.Invalid<MembershipDto>("A time-limited role must end in the future, and at most five years from now.");
        }

        DateTimeOffset? expiresAt = input.ExpiresAt?.ToUniversalTime();
        HashSet<Guid?> wanted = codes.Count == 0 ? [null] : [.. roles.Values.Select(r => (Guid?)r.Id)];
        List<OrgMembership> current = await _db.OrgMemberships
            .Where(m => m.AppId == appId && m.OrgId == orgId && m.UserId == userId && m.RevokedAt == null)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        bool changed = false;
        foreach (OrgMembership gone in current.Where(m => !wanted.Contains(m.RoleId)))
        {
            gone.RevokedAt = now;
            gone.RevokedByUserId = actor.UserId;
            changed = true;
        }

        DateTimeOffset grantedAt = current.Count > 0 ? current.Min(m => m.GrantedAt) : now;
        foreach (Guid? roleId in wanted)
        {
            OrgMembership? held = current.FirstOrDefault(m => m.RoleId == roleId);
            if (held is null)
            {
                _db.OrgMemberships.Add(new OrgMembership { Id = Guid.NewGuid(), AppId = appId, OrgId = orgId, UserId = userId, RoleId = roleId, GrantedAt = now, GrantedByUserId = actor.UserId, ExpiresAt = expiresAt });
                changed = true;
            }
            else if (held.ExpiresAt != expiresAt)
            {
                // rc.11: stored in UTC whatever offset the caller used (the partner console sends the end of an India day).
                held.ExpiresAt = expiresAt;
                changed = true;
            }
        }

        bool hasGrant = await _db.AppGrants.AnyAsync(g => g.UserId == userId && g.AppId == appId && g.RevokedAt == null, cancellationToken).ConfigureAwait(false);
        if (!hasGrant)
        {
            _db.AppGrants.Add(new AppGrant { Id = Guid.NewGuid(), UserId = userId, AppId = appId, GrantedAt = now });
            await AppEventLog.AddAsync(_db, AppEventTypes.UserCreated, appId, userId, null, null, now, cancellationToken).ConfigureAwait(false);
        }

        if (changed)
        {
            await AppEventLog.AddAsync(_db, AppEventTypes.MembershipGranted, appId, userId, orgId, new Dictionary<string, object?>
            {
                ["org_id"] = orgId,
                ["role"] = codes.Count > 0 ? codes[0] : null,
                ["roles"] = codes,
                ["applies_to_descendants"] = false,
                ["expires_at"] = expiresAt,
            }, now, cancellationToken).ConfigureAwait(false);
        }

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await _audit.WriteAsync(
            new AuditEntry(AuditActions.OrgMembershipGrant, actor.Type, actor.UserId, ActorAppId: appId, TargetType: "user", TargetId: userId,
                Metadata: actor.Reason is { Length: > 0 } why
                    ? JsonSerializer.Serialize(new { org = orgId, role = codes.Count > 0 ? codes[0] : null, roles = codes, expires_at = expiresAt, reason = why })
                    : JsonSerializer.Serialize(new { org = orgId, role = codes.Count > 0 ? codes[0] : null, roles = codes, expires_at = expiresAt })),
            cancellationToken).ConfigureAwait(false);
        return ManagementResult.Ok<MembershipDto>(Dto(userId, orgId, codes, grantedAt, expiresAt));
    }

    /// <inheritdoc />
    public async Task<ManagementResult<MembershipDto>> RevokeMembershipAsync(Guid appId, Guid orgId, Guid userId, ManagementActor actor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        List<OrgMembership> held = await _db.OrgMemberships.Include(m => m.Role)
            .Where(m => m.AppId == appId && m.OrgId == orgId && m.UserId == userId && m.RevokedAt == null)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        if (held.Count == 0)
        {
            return ManagementResult.NotFound<MembershipDto>("No live membership for that user in that organisation.");
        }

        DateTimeOffset revokedAt = _clock.UtcNow;
        List<string> codes = [.. held.Where(m => m.Role is not null).Select(m => m.Role!.Code).Order(StringComparer.Ordinal)];
        foreach (OrgMembership membership in held)
        {
            membership.RevokedAt = revokedAt;
            membership.RevokedByUserId = actor.UserId;
        }

        List<Guid> ids = [.. held.Select(m => m.Id)];
        await AppEventLog.AddAsync(_db, AppEventTypes.MembershipRevoked, appId, userId, orgId, new Dictionary<string, object?> { ["org_id"] = orgId, ["role"] = codes.Count > 0 ? codes[0] : null, ["roles"] = codes }, revokedAt, cancellationToken).ConfigureAwait(false);
        if (!await _db.OrgMemberships.AnyAsync(m => m.AppId == appId && m.UserId == userId && m.RevokedAt == null && (m.ExpiresAt == null || m.ExpiresAt > DateTimeOffset.UtcNow) && !ids.Contains(m.Id), cancellationToken).ConfigureAwait(false))
        {
            await AppEventLog.AddAsync(_db, AppEventTypes.UserDeactivated, appId, userId, null, new Dictionary<string, object?> { ["reason"] = "last_role_revoked" }, revokedAt, cancellationToken).ConfigureAwait(false);
        }

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await _audit.WriteAsync(
            new AuditEntry(AuditActions.OrgMembershipRevoke, actor.Type, actor.UserId, ActorAppId: appId, TargetType: "user", TargetId: userId,
                Metadata: actor.Reason is { Length: > 0 } why
                    ? JsonSerializer.Serialize(new { org = orgId.ToString("D"), role = codes.Count > 0 ? codes[0] : null, roles = codes, reason = why })
                    : JsonSerializer.Serialize(new { org = orgId.ToString("D"), roles = codes })),
            cancellationToken).ConfigureAwait(false);

        // rc.12: an admin right ends with the person's last place in its tree.
        await OrgAdminCleanup.RunAsync(_db, _audit, appId, userId, revokedAt, cancellationToken).ConfigureAwait(false);
        return ManagementResult.Ok<MembershipDto>(Dto(userId, orgId, codes, held.Min(m => m.GrantedAt), null));
    }

    /// <summary>rc.12: whether roles for <paramref name="org"/> are kept in Sangam (its customer's setting; yes unless set to no).</summary>
    private async Task<bool> RolesKeptInSangamAsync(Organisation org, CancellationToken cancellationToken)
    {
        Guid customerId = OrganisationPath.CustomerId(org.Path);
        bool? kept = customerId == org.Id
            ? org.RolesKeptInSangam
            : await _db.Organisations.AsNoTracking().Where(o => o.Id == customerId).Select(o => o.RolesKeptInSangam).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return kept != false;
    }

    private static MembershipDto Dto(Guid userId, Guid orgId, IEnumerable<string?> roles, DateTimeOffset grantedAt, DateTimeOffset? expiresAt)
    {
        List<string> codes = [.. roles.Where(r => r is not null).Select(r => r!).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
        return new MembershipDto(userId, orgId, codes.Count > 0 ? codes[0] : null, false, grantedAt, expiresAt, codes);
    }

    /// <summary>An app sees an organisation it registered, or any organisation where it holds a membership.</summary>
    private async Task<bool> CanSeeAsync(Guid appId, Organisation org, CancellationToken cancellationToken)
        => org.RegisteredViaAppId == appId
        || await _db.OrgMemberships.AnyAsync(m => m.AppId == appId && m.OrgId == org.Id, cancellationToken).ConfigureAwait(false);

    private static RoleDto ToDto(Role r) => new(r.Code, r.DisplayName, r.Description, EfTenancyQuery.ParsePermissions(r.Permissions), r.OrgId, r.IsSystem, r.RetiredAt);

    private static OrganisationDto ToDto(Organisation o) => new(o.Id, o.Name, o.OrgTypeCode, o.ParentOrgId, o.Path, o.Depth, o.Status, o.Metadata);

    private static bool IsJsonObject(string json)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    [GeneratedRegex("^[a-z][a-z0-9_]{1,49}$")]
    private static partial Regex RoleCodeRegex();
}
