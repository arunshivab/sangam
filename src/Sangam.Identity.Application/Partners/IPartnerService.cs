using Sangam.Identity.Application.Tenancy;
using Sangam.Identity.Domain.Enums;

namespace Sangam.Identity.Application.Partners;

/// <summary>
/// What a partner's own staff may do on the partner console, over the applications they
/// administer. Every method takes the acting person and the application: authorisation is
/// decided here by their rank over <em>that</em> application, never in the UI, and every change
/// is audited in their name.
/// <para>
/// Deliberately absent: changing redirect URIs or client secrets (a redirect URI is where sign-in
/// codes are sent, so one compromised account could harvest every user's sign-in), seeing people
/// who have not linked the application, and weakening its sign-in policy. See ADR-0006.
/// </para>
/// </summary>
public interface IPartnerService
{
    /// <summary>The applications the person administers, with their rank over each.</summary>
    Task<IReadOnlyList<PartnerAppRow>> GetMyAppsAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>Their rank over one application, or <see langword="null"/> when they administer it not at all.</summary>
    Task<AppAdminRole?> GetRoleAsync(Guid userId, Guid appId, CancellationToken cancellationToken = default);

    /// <summary>The application's roles.</summary>
    Task<IReadOnlyList<RoleDto>> ListRolesAsync(Guid userId, Guid appId, CancellationToken cancellationToken = default);

    /// <summary>Creates or updates a role.</summary>
    Task<PartnerResult> UpsertRoleAsync(Guid userId, Guid appId, string code, RoleUpsert input, CancellationToken cancellationToken = default);

    /// <summary>Retires a role.</summary>
    Task<PartnerResult> RetireRoleAsync(Guid userId, Guid appId, string code, CancellationToken cancellationToken = default);

    /// <summary>The organisations registered through the application, in tree order.</summary>
    Task<IReadOnlyList<OrganisationDto>> ListOrganisationsAsync(Guid userId, Guid appId, CancellationToken cancellationToken = default);

    /// <summary>The kinds of organisation Sangam knows, in display order. Not scoped: the list is the same for everyone.</summary>
    Task<IReadOnlyList<OrgTypeRow>> ListOrgTypesAsync(CancellationToken cancellationToken = default);

    /// <summary>Creates or renames an organisation.</summary>
    Task<PartnerResult> UpsertOrganisationAsync(Guid userId, Guid appId, Guid orgId, OrganisationUpsert input, CancellationToken cancellationToken = default);

    /// <summary>Finds people who have linked the application — and only them.</summary>
    Task<IReadOnlyList<LinkedUserRow>> SearchLinkedUsersAsync(Guid userId, Guid appId, string? query, int take, CancellationToken cancellationToken = default);

    /// <summary>The live members of one organisation.</summary>
    Task<IReadOnlyList<PartnerMemberRow>> ListMembersAsync(Guid userId, Guid appId, Guid orgId, CancellationToken cancellationToken = default);

    /// <summary>Gives a person a role in an organisation. Refused unless they have linked the application.</summary>
    Task<PartnerResult> GrantMembershipAsync(Guid userId, Guid appId, Guid orgId, Guid memberUserId, MembershipUpsert input, CancellationToken cancellationToken = default);

    /// <summary>Removes a person's role in an organisation.</summary>
    Task<PartnerResult> RevokeMembershipAsync(Guid userId, Guid appId, Guid orgId, Guid memberUserId, CancellationToken cancellationToken = default);

    /// <summary>The application's administrators. Owners only.</summary>
    Task<IReadOnlyList<AppAdminRow>> ListAdminsAsync(Guid userId, Guid appId, CancellationToken cancellationToken = default);

    /// <summary>Makes a person who has linked the application one of its administrators. Owners only.</summary>
    Task<PartnerResult> GrantAdminAsync(Guid userId, Guid appId, string email, AppAdminRole role, CancellationToken cancellationToken = default);

    /// <summary>Removes an administrator. Owners only, and never the last owner.</summary>
    Task<PartnerResult> RevokeAdminAsync(Guid userId, Guid appId, Guid adminUserId, CancellationToken cancellationToken = default);

    /// <summary>The settings a partner may see and change.</summary>
    Task<PartnerAppSettings?> GetSettingsAsync(Guid userId, Guid appId, CancellationToken cancellationToken = default);

    /// <summary>Updates branding and sign-in policy. The policy may only be made stricter, never weaker.</summary>
    Task<PartnerResult> UpdateSettingsAsync(Guid userId, Guid appId, string? description, string brandColour, string glyph, SignInPolicy signInPolicy, CancellationToken cancellationToken = default);

    /// <summary>The application's password and second-factor policy, against the platform's (PR-16).</summary>
    /// <param name="userId">The administrator.</param>
    /// <param name="appId">The application.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<PolicyView?> GetAppPolicyAsync(Guid userId, Guid appId, CancellationToken cancellationToken = default);

    /// <summary>Sets the application's password and second-factor policy; never weaker than the platform's (PR-16).</summary>
    /// <param name="userId">The administrator.</param>
    /// <param name="appId">The application.</param>
    /// <param name="input">The settings; the sign-in rule here is ignored (it is set with the other settings).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<PartnerResult> UpdateAppPolicyAsync(Guid userId, Guid appId, PolicyInput input, CancellationToken cancellationToken = default);

    /// <summary>An organisation's security policy, against what it inherits (PR-16).</summary>
    /// <param name="userId">The administrator.</param>
    /// <param name="appId">The application.</param>
    /// <param name="orgId">The organisation.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<PolicyView?> GetOrganisationPolicyAsync(Guid userId, Guid appId, Guid orgId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets an organisation's security policy, which also applies to every organisation below it; never weaker
    /// than what it inherits from the application and its parents (PR-16, SGM-209 §7).
    /// </summary>
    /// <param name="userId">The administrator.</param>
    /// <param name="appId">The application.</param>
    /// <param name="orgId">The organisation.</param>
    /// <param name="input">The settings.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<PartnerResult> UpdateOrganisationPolicyAsync(Guid userId, Guid appId, Guid orgId, PolicyInput input, CancellationToken cancellationToken = default);
}
