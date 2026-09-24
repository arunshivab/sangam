namespace Sangam.Identity.Application.Tenancy;

/// <summary>One element of the <c>sangam_orgs</c> claim: a membership scoped to the requesting app.</summary>
/// <param name="Id">Organisation id.</param>
/// <param name="Name">Organisation display name.</param>
/// <param name="Type">Organisation type code.</param>
/// <param name="Path">Materialised ancestor path.</param>
/// <param name="Role">Role code in the app's vocabulary.</param>
/// <param name="Permissions">Permission strings from the role definition.</param>
/// <param name="Inherits">Whether the role also applies to descendant organisations.</param>
public sealed record OrgClaim(Guid Id, string Name, string Type, string Path, string Role, IReadOnlyList<string> Permissions, bool Inherits);
