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

        if (input.Metadata is not null && !IsJsonObject(input.Metadata))
        {
            return ManagementResult.Invalid<OrganisationDto>("Metadata must be a JSON object.");
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
            .Select(m => new { m.UserId, m.OrgId, Role = m.Role!.Code, m.AppliesToDescendants, m.GrantedAt, m.ExpiresAt })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return [.. rows.Select(r => new MembershipDto(r.UserId, r.OrgId, r.Role, r.AppliesToDescendants, r.GrantedAt, r.ExpiresAt))];
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

        bool userExists = await _db.Users.AnyAsync(u => u.Id == userId && u.Status == UserStatus.Active, cancellationToken).ConfigureAwait(false);
        if (!userExists)
        {
            return ManagementResult.NotFound<MembershipDto>("The user does not exist or is not active.");
        }

        // Org-scoped role for this org wins over the app-wide one of the same code.
        // (Order on the predicate, not on OrgId: PostgreSQL sorts NULLS FIRST on DESC.)
        Role? role = await _db.Roles.AsNoTracking()
            .Where(r => r.AppId == appId && r.Code == input.Role && r.RetiredAt == null && (r.OrgId == null || r.OrgId == orgId))
            .OrderByDescending(r => r.OrgId != null)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (role is null)
        {
            return ManagementResult.NotFound<MembershipDto>("No live role with that code for this app.");
        }

        DateTimeOffset now = _clock.UtcNow;
        if (input.ExpiresAt is DateTimeOffset until && (until <= now || until > now.AddYears(5)))
        {
            return ManagementResult.Invalid<MembershipDto>("A time-limited role must end in the future, and at most five years from now.");
        }

        OrgMembership? membership = await _db.OrgMemberships.FirstOrDefaultAsync(m => m.AppId == appId && m.OrgId == orgId && m.UserId == userId && m.RevokedAt == null, cancellationToken).ConfigureAwait(false);
        bool changed = membership is null || membership.RoleId != role.Id || membership.AppliesToDescendants != input.AppliesToDescendants || membership.ExpiresAt != input.ExpiresAt;
        if (membership is null)
        {
            membership = new OrgMembership { Id = Guid.NewGuid(), AppId = appId, OrgId = orgId, UserId = userId, GrantedAt = now, GrantedByUserId = actor.UserId };
            _db.OrgMemberships.Add(membership);
        }

        membership.RoleId = role.Id;
        membership.AppliesToDescendants = input.AppliesToDescendants;
        membership.ExpiresAt = input.ExpiresAt;

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
                ["role"] = role.Code,
                ["applies_to_descendants"] = input.AppliesToDescendants,
                ["expires_at"] = input.ExpiresAt,
            }, now, cancellationToken).ConfigureAwait(false);
        }

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await _audit.WriteAsync(
            new AuditEntry(AuditActions.OrgMembershipGrant, actor.Type, actor.UserId, ActorAppId: appId, TargetType: "user", TargetId: userId,
                Metadata: JsonSerializer.Serialize(new { org = orgId, role = role.Code, inherits = input.AppliesToDescendants, expires_at = input.ExpiresAt })),
            cancellationToken).ConfigureAwait(false);
        return ManagementResult.Ok<MembershipDto>(new MembershipDto(userId, orgId, role.Code, membership.AppliesToDescendants, membership.GrantedAt, membership.ExpiresAt));
    }

    /// <inheritdoc />
    public async Task<ManagementResult<MembershipDto>> RevokeMembershipAsync(Guid appId, Guid orgId, Guid userId, ManagementActor actor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        OrgMembership? membership = await _db.OrgMemberships.Include(m => m.Role)
            .FirstOrDefaultAsync(m => m.AppId == appId && m.OrgId == orgId && m.UserId == userId && m.RevokedAt == null, cancellationToken).ConfigureAwait(false);
        if (membership is null)
        {
            return ManagementResult.NotFound<MembershipDto>("No live membership for that user in that organisation.");
        }

        DateTimeOffset revokedAt = _clock.UtcNow;
        membership.RevokedAt = revokedAt;
        membership.RevokedByUserId = actor.UserId;
        await AppEventLog.AddAsync(_db, AppEventTypes.MembershipRevoked, appId, userId, orgId, new Dictionary<string, object?> { ["org_id"] = orgId, ["role"] = membership.Role!.Code }, revokedAt, cancellationToken).ConfigureAwait(false);
        if (!await _db.OrgMemberships.AnyAsync(m => m.AppId == appId && m.UserId == userId && m.RevokedAt == null && (m.ExpiresAt == null || m.ExpiresAt > DateTimeOffset.UtcNow) && m.Id != membership.Id, cancellationToken).ConfigureAwait(false))
        {
            await AppEventLog.AddAsync(_db, AppEventTypes.UserDeactivated, appId, userId, null, new Dictionary<string, object?> { ["reason"] = "last_role_revoked" }, revokedAt, cancellationToken).ConfigureAwait(false);
        }

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await _audit.WriteAsync(
            new AuditEntry(AuditActions.OrgMembershipRevoke, actor.Type, actor.UserId, ActorAppId: appId, TargetType: "user", TargetId: userId, Metadata: $"{{\"org\":\"{orgId:D}\"}}"),
            cancellationToken).ConfigureAwait(false);
        return ManagementResult.Ok<MembershipDto>(new MembershipDto(userId, orgId, membership.Role!.Code, membership.AppliesToDescendants, membership.GrantedAt));
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
