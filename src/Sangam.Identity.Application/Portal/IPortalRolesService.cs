namespace Sangam.Identity.Application.Portal;

/// <summary>
/// rc.10 (P4; founder decisions, 12 Oct 2026): the roles applications have given a person, grouped by organisation, and
/// leaving an organisation. Roles are each application's own words, shown as the application named them; Sangam never
/// interprets them. Leaving ends the person's roles held at that organisation through the same path a partner uses, so
/// each application is told; the last organisation admin of an application cannot leave it stranded.
/// </summary>
public interface IPortalRolesService
{
    /// <summary>The person's live roles, grouped by the organisation that holds them, the most recently given first.</summary>
    /// <param name="userId">The person.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The organisations.</returns>
    Task<IReadOnlyList<PortalOrganisation>> GetAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Ends every role the person holds at <paramref name="orgId"/>, except in an application where they are its only
    /// organisation admin there. Each ending is audited with the person as the actor and sent to the application; the
    /// person and that organisation's organisation admins are told by e-mail.
    /// </summary>
    /// <param name="userId">The person.</param>
    /// <param name="orgId">The organisation.</param>
    /// <param name="ipAddress">Their address, for the audit trail.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>What ended and what stayed.</returns>
    Task<LeaveResult> LeaveAsync(Guid userId, Guid orgId, string? ipAddress, CancellationToken cancellationToken = default);
}

/// <summary>One role, as the person sees it.</summary>
/// <param name="AppId">The application.</param>
/// <param name="AppName">Its name.</param>
/// <param name="RoleName">The role as the application named it (its code when it gave no name).</param>
/// <param name="GivenBy">Who gave it, when known.</param>
/// <param name="GivenAt">When.</param>
/// <param name="Until">When it ends, for a time-limited role.</param>
/// <param name="OnlyOrganisationAdmin">Whether the person is this application's only organisation admin here (they cannot leave it).</param>
public sealed record PortalRole(Guid AppId, string AppName, string RoleName, string? GivenBy, DateTimeOffset GivenAt, DateTimeOffset? Until, bool OnlyOrganisationAdmin);

/// <summary>An organisation the person holds roles in.</summary>
/// <param name="OrgId">The organisation.</param>
/// <param name="Name">Its name.</param>
/// <param name="Within">The organisations above it, outermost first, for example "Apulki Group".</param>
/// <param name="Roles">The roles held there.</param>
public sealed record PortalOrganisation(Guid OrgId, string Name, string? Within, IReadOnlyList<PortalRole> Roles)
{
    /// <summary>Whether any of the roles here can be left.</summary>
    public bool CanLeave => Roles.Any(r => !r.OnlyOrganisationAdmin);
}

/// <summary>What leaving did.</summary>
/// <param name="Succeeded">Whether any role ended.</param>
/// <param name="Ended">The applications whose roles ended.</param>
/// <param name="Kept">The applications kept because the person is their only organisation admin here.</param>
public sealed record LeaveResult(bool Succeeded, IReadOnlyList<string> Ended, IReadOnlyList<string> Kept);
