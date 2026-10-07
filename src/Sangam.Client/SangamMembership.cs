namespace Sangam.Client;

/// <summary>
/// One role the person holds in one organisation of this application, as Sangam reported it at
/// sign-in. Roles and permissions are this application's own vocabulary; Sangam never
/// interprets them.
/// </summary>
/// <param name="OrganisationId">The organisation.</param>
/// <param name="OrganisationName">Its name.</param>
/// <param name="OrganisationType">Its kind, such as <c>hospital</c> or <c>department</c>.</param>
/// <param name="Path">Where it sits in its tree: <c>/{root}/…/{self}/</c>.</param>
/// <param name="Role">The role's code, such as <c>doctor</c>.</param>
/// <param name="Permissions">The role's permissions, as this application defined them.</param>
/// <param name="AppliesToDescendants">Whether the role also holds in every organisation beneath this one.</param>
public sealed record SangamMembership(
    Guid OrganisationId,
    string OrganisationName,
    string OrganisationType,
    string Path,
    string Role,
    IReadOnlyList<string> Permissions,
    bool AppliesToDescendants)
{
    /// <summary>
    /// Whether this role holds in the organisation at <paramref name="organisationPath"/>: that
    /// organisation itself, or one beneath it when the role applies to descendants.
    /// </summary>
    /// <param name="organisationPath">The organisation's path, as Sangam reports it.</param>
    /// <returns><see langword="true"/> when the role holds there.</returns>
    public bool Covers(string organisationPath)
    {
        ArgumentNullException.ThrowIfNull(organisationPath);
        if (string.Equals(organisationPath, Path, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Paths end with '/', so "/a/" can never match a sibling "/ab/".
        return AppliesToDescendants
            && Path.EndsWith('/')
            && organisationPath.StartsWith(Path, StringComparison.OrdinalIgnoreCase);
    }
}
