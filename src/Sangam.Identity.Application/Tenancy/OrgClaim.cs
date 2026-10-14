namespace Sangam.Identity.Application.Tenancy;

/// <summary>One element of the <c>sangam_orgs</c> claim: the person's place in one organisation, for the requesting app.</summary>
/// <param name="Id">Organisation id.</param>
/// <param name="Name">Organisation display name.</param>
/// <param name="Type">Organisation type code.</param>
/// <param name="Path">Materialised ancestor path.</param>
/// <param name="Role">The first of <paramref name="Roles"/> (kept for clients written before rc.12), or null.</param>
/// <param name="Permissions">Permission strings from every role held there.</param>
/// <param name="Inherits">Always false since rc.12 (a membership is in its own organisation only); kept for existing clients.</param>
/// <param name="Roles">rc.12: every role code held there; empty for staff whose roles the application keeps.</param>
public sealed record OrgClaim(Guid Id, string Name, string Type, string Path, string? Role, IReadOnlyList<string> Permissions, bool Inherits, IReadOnlyList<string>? Roles = null);
