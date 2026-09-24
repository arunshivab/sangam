namespace Sangam.Identity.Application.Tenancy;

/// <summary>
/// The management operations a partner app performs with its client credentials. Every
/// method is scoped to one app: an app can only ever see and change its own roles,
/// organisations registered through it, and memberships in its own context.
/// </summary>
public interface IManagementService
{
    /// <summary>Lists the app's roles (app-wide and org-scoped), retired ones included.</summary>
    Task<IReadOnlyList<RoleDto>> ListRolesAsync(Guid appId, CancellationToken cancellationToken = default);

    /// <summary>Creates or updates a role. Codes are lowercase snake_case; the system <c>org_admin</c> cannot be re-scoped.</summary>
    Task<ManagementResult<RoleDto>> UpsertRoleAsync(Guid appId, string code, RoleUpsert input, CancellationToken cancellationToken = default);

    /// <summary>Retires a role. Existing memberships keep working until reassigned; the system role cannot be retired.</summary>
    Task<ManagementResult<RoleDto>> RetireRoleAsync(Guid appId, string code, CancellationToken cancellationToken = default);

    /// <summary>Gets an organisation the app may see (registered through it, or one it has memberships in).</summary>
    Task<OrganisationDto?> GetOrganisationAsync(Guid appId, Guid orgId, CancellationToken cancellationToken = default);

    /// <summary>Creates (with the app as registrar) or updates an organisation.</summary>
    Task<ManagementResult<OrganisationDto>> UpsertOrganisationAsync(Guid appId, Guid orgId, OrganisationUpsert input, CancellationToken cancellationToken = default);

    /// <summary>Lists live memberships in an organisation for this app.</summary>
    Task<IReadOnlyList<MembershipDto>> ListMembersAsync(Guid appId, Guid orgId, CancellationToken cancellationToken = default);

    /// <summary>Grants a membership (or changes the role); creates the app grant if missing.</summary>
    Task<ManagementResult<MembershipDto>> UpsertMembershipAsync(Guid appId, Guid orgId, Guid userId, MembershipUpsert input, CancellationToken cancellationToken = default);

    /// <summary>Revokes a membership. Returns <see cref="ManagementStatus.NotFound"/> when there is none.</summary>
    Task<ManagementResult<MembershipDto>> RevokeMembershipAsync(Guid appId, Guid orgId, Guid userId, CancellationToken cancellationToken = default);
}
