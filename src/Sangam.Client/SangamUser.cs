namespace Sangam.Client;

/// <summary>
/// The person signed in with Sangam, as this application sees them: who they are and which roles
/// they hold where. Read it with <see cref="SangamClaimsPrincipalExtensions.GetSangamUser"/>.
/// Roles are as of sign-in; a role granted later arrives the next time the person signs in.
/// </summary>
/// <param name="Id">Their Sangam user id — stable, and the key to store against them.</param>
/// <param name="Name">Their name.</param>
/// <param name="Email">Their email address, if this application asked for it.</param>
/// <param name="Memberships">Every role they hold in this application's organisations.</param>
public sealed record SangamUser(Guid Id, string Name, string? Email, IReadOnlyList<SangamMembership> Memberships)
{
    /// <summary>The claim carrying the user id.</summary>
    public const string SubjectClaim = "sub";

    /// <summary>The claim carrying the person's name.</summary>
    public const string NameClaim = "name";

    /// <summary>The claim carrying the person's email address.</summary>
    public const string EmailClaim = "email";

    /// <summary>The organisations they hold any role in, once each, in the order Sangam sent them.</summary>
    public IReadOnlyList<SangamMembership> Organisations
        => [.. Memberships.GroupBy(m => m.OrganisationId).Select(g => g.First())];

    /// <summary>The roles that hold in the organisation at <paramref name="organisationPath"/>, including inherited ones.</summary>
    /// <param name="organisationPath">The organisation's path.</param>
    /// <returns>Distinct role codes.</returns>
    public IReadOnlyList<string> RolesIn(string organisationPath)
    {
        ArgumentNullException.ThrowIfNull(organisationPath);
        return [.. Memberships.Where(m => m.Covers(organisationPath)).Select(m => m.Role).Distinct(StringComparer.Ordinal)];
    }

    /// <summary>Whether they hold <paramref name="role"/> in the organisation at <paramref name="organisationPath"/>.</summary>
    /// <param name="role">The role code.</param>
    /// <param name="organisationPath">The organisation's path.</param>
    /// <returns><see langword="true"/> when they do.</returns>
    public bool HasRole(string role, string organisationPath)
    {
        ArgumentNullException.ThrowIfNull(role);
        ArgumentNullException.ThrowIfNull(organisationPath);
        return Memberships.Any(m => string.Equals(m.Role, role, StringComparison.Ordinal) && m.Covers(organisationPath));
    }

    /// <summary>Whether any role they hold in the organisation at <paramref name="organisationPath"/> grants <paramref name="permission"/>.</summary>
    /// <param name="permission">The permission, as this application defined it.</param>
    /// <param name="organisationPath">The organisation's path.</param>
    /// <returns><see langword="true"/> when one does.</returns>
    public bool HasPermission(string permission, string organisationPath)
    {
        ArgumentNullException.ThrowIfNull(permission);
        ArgumentNullException.ThrowIfNull(organisationPath);
        return Memberships.Any(m => m.Covers(organisationPath) && m.Permissions.Contains(permission, StringComparer.Ordinal));
    }

    /// <summary>The path Sangam reported for an organisation they hold a role in, or <see langword="null"/>.</summary>
    /// <param name="organisationId">The organisation.</param>
    /// <returns>Its path, or <see langword="null"/> when they hold no role there.</returns>
    public string? PathOf(Guid organisationId) => Memberships.FirstOrDefault(m => m.OrganisationId == organisationId)?.Path;
}
