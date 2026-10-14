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
/// <param name="Role">The role's code, such as <c>doctor</c>. Empty when the person belongs here without a role in Sangam
/// (the application keeps its roles itself).</param>
/// <param name="Permissions">The permissions of every role the person holds in this organisation, as this application
/// defined them.</param>
/// <param name="AppliesToDescendants">Whether the role also holds beneath this organisation. Since rc.12 a role holds only
/// where it was given, so Sangam always sends <see langword="false"/>.</param>
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

/// <summary>rc.12 (A6): the organisation a person chose to work at for this sign-in.</summary>
/// <param name="OrganisationId">The organisation.</param>
/// <param name="OrganisationName">Its name.</param>
/// <param name="OrganisationType">Its kind.</param>
/// <param name="Path">Where it sits in its tree.</param>
/// <param name="Roles">Every role held there; empty when your application keeps roles itself.</param>
/// <param name="Permissions">The permissions of those roles.</param>
public sealed record SangamCurrentOrganisation(Guid OrganisationId, string OrganisationName, string OrganisationType, string Path, IReadOnlyList<string> Roles, IReadOnlyList<string> Permissions);

/// <summary>
/// rc.12 (M12): emergency access in progress, from the <c>sangam_emergency</c> claim. Show a banner, apply your own limits,
/// and stop at <see cref="Until"/> even if the token lives longer.
/// </summary>
/// <param name="Id">The use, as Sangam records it.</param>
/// <param name="CaseReference">Your own case reference, as you sent it.</param>
/// <param name="OrganisationId">The organisation the person is working at.</param>
/// <param name="Until">When it ends at the latest (60 minutes after the start).</param>
public sealed record SangamEmergency(Guid Id, string CaseReference, Guid OrganisationId, DateTimeOffset Until)
{
    /// <summary>Whether it is still in force at <paramref name="now"/>.</summary>
    /// <param name="now">Now.</param>
    /// <returns><see langword="true"/> before <see cref="Until"/>.</returns>
    public bool IsActive(DateTimeOffset now) => now < Until;
}
