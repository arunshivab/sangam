using Sangam.Identity.Application.Partners;
using Sangam.Identity.Application.Tenancy;

namespace Sangam.Partner.Web.Components.Common;

/// <summary>The signed-in administrator, cascaded to every page once the gate is cleared.</summary>
/// <param name="UserId">Their Sangam user id.</param>
/// <param name="Name">Their display name.</param>
/// <param name="Apps">The applications they administer, with their rank over each, as of this visit.</param>
public sealed record PartnerContext(Guid UserId, string Name, IReadOnlyList<PartnerAppRow> Apps)
{
    /// <summary>How strongly this session was signed in (rc.10): 2 two-step sign-in, 3 a passkey.</summary>
    public int SignInLevel { get; init; }

    /// <summary>When the session ends at the latest; <see langword="null"/> when the cookie does not say.</summary>
    public DateTimeOffset? SessionEndsAt { get; init; }

    /// <summary>
    /// rc.12: the organisation trees they administer (customer and branch admins, emergency access reviewers, and every
    /// customer of an application they administer), for the organisation console.
    /// </summary>
    public IReadOnlyList<OrgScope> Scopes { get; init; } = [];

    /// <summary>The trees they hold a right in themselves, not through administering the whole application.</summary>
    public IEnumerable<OrgScope> OwnScopes => Scopes.Where(s => !s.ViaApplication);

    /// <summary>The tree an organisation belongs to, when they administer or review it.</summary>
    /// <param name="orgId">The organisation (the tree's top, as the console addresses it).</param>
    /// <returns>The tree, or <see langword="null"/>.</returns>
    public OrgScope? Scope(Guid orgId) => Scopes.FirstOrDefault(s => s.OrgId == orgId);
}
