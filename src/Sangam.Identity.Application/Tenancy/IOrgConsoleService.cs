using Sangam.Identity.Application.Partners;

namespace Sangam.Identity.Application.Tenancy;

/// <summary>
/// rc.12: one tree a person administers: an organisation in one application, and everything below it. A customer admin's
/// right is at the top organisation; a branch admin's at a branch; an application admin administers every customer.
/// </summary>
/// <param name="AppId">The application.</param>
/// <param name="AppName">Its name.</param>
/// <param name="BrandColour">Its colour, for the tile.</param>
/// <param name="Glyph">Its glyph, for the tile.</param>
/// <param name="OrgId">The organisation the right is at.</param>
/// <param name="OrgName">Its name.</param>
/// <param name="Path">Its path.</param>
/// <param name="IsCustomer">Whether it is a customer (a top organisation), not a branch.</param>
/// <param name="ViaApplication">Whether the person is an admin of the whole application, rather than of this tree.</param>
/// <param name="Admin">Whether the person administers the tree (otherwise they only review emergency access in it).</param>
/// <param name="Reviewer">Whether the person reviews emergency access here.</param>
public sealed record OrgScope(Guid AppId, string AppName, string BrandColour, string Glyph, Guid OrgId, string OrgName, string Path, bool IsCustomer, bool ViaApplication, bool Admin = true, bool Reviewer = false);

/// <summary>The numbers and decisions on a tree's overview (G1).</summary>
/// <param name="Staff">People with a place anywhere in the tree.</param>
/// <param name="RolesEnding">Roles ending in the next 14 days.</param>
/// <param name="Waiting">Additions still waiting (not activated, or not started).</param>
/// <param name="EmergencyUses">Emergency access uses in the last 7 days.</param>
/// <param name="Decisions">What needs a decision, soonest first.</param>
/// <param name="RolesKeptInSangam">Whether this customer keeps its roles in Sangam (else in the application).</param>
/// <param name="EmergencyAccess">Whether emergency access is switched on for this organisation.</param>
public sealed record OrgOverview(int Staff, int RolesEnding, int Waiting, int EmergencyUses, IReadOnlyList<OrgDecision> Decisions, bool RolesKeptInSangam, bool EmergencyAccess);

/// <summary>What a decision is about.</summary>
public enum OrgDecisionKind
{
    /// <summary>A role ends soon.</summary>
    RoleEnding,

    /// <summary>A person was added and has not activated.</summary>
    NotJoined,

    /// <summary>A person is leaving on a date.</summary>
    Leaving,

    /// <summary>An emergency access use waits for review.</summary>
    EmergencyUse,
}

/// <summary>One thing needing a decision.</summary>
/// <param name="Kind">What it is.</param>
/// <param name="Name">Who.</param>
/// <param name="OrgName">Where.</param>
/// <param name="Detail">The roles, or the application.</param>
/// <param name="When">When it ends, was added, or was used.</param>
/// <param name="UserId">The person, when they have an account here.</param>
/// <param name="OrgId">The organisation.</param>
/// <param name="ItemId">The waiting addition or the emergency use, when it is one.</param>
public sealed record OrgDecision(OrgDecisionKind Kind, string Name, string OrgName, string Detail, DateTimeOffset When, Guid? UserId, Guid OrgId, Guid? ItemId = null);

/// <summary>An organisation in a tree, with how many people it has.</summary>
/// <param name="Id">The organisation.</param>
/// <param name="Name">Its name.</param>
/// <param name="ParentId">Its parent, inside the tree.</param>
/// <param name="Depth">How far below the tree's top.</param>
/// <param name="People">People with a place there.</param>
/// <param name="EmergencyAccess">rc.12 (M12): whether this organisation itself has switched emergency access on.</param>
public sealed record OrgNode(Guid Id, string Name, Guid? ParentId, int Depth, int People, bool EmergencyAccess = false);

/// <summary>One person's place at an organisation.</summary>
/// <param name="UserId">The person.</param>
/// <param name="Name">Their name.</param>
/// <param name="Email">Their email.</param>
/// <param name="Roles">Their role codes there (none for staff whose roles the application keeps).</param>
/// <param name="Since">Since when.</param>
/// <param name="Until">Until when, if it ends.</param>
/// <param name="Admin">Whether they are an admin here.</param>
/// <param name="Reviewer">Whether they review emergency access here.</param>
public sealed record OrgPerson(Guid UserId, string Name, string Email, IReadOnlyList<string> Roles, DateTimeOffset Since, DateTimeOffset? Until, bool Admin, bool Reviewer);

/// <summary>A role an application offers, to choose from when adding someone.</summary>
/// <param name="Code">Its code.</param>
/// <param name="Name">Its name, as the application gave it.</param>
public sealed record OrgRoleChoice(string Code, string Name);

/// <summary>Someone the admin can see, for the leaver form: their places in every tree the admin administers.</summary>
/// <param name="UserId">The person.</param>
/// <param name="Name">Their name.</param>
/// <param name="Email">Their email, masked.</param>
/// <param name="Places">Each place, by application and organisation.</param>
public sealed record OrgLeaver(Guid UserId, string Name, string Email, IReadOnlyList<OrgLeaverPlace> Places);

/// <summary>One of a leaver's places.</summary>
/// <param name="AppId">The application.</param>
/// <param name="AppName">Its name.</param>
/// <param name="OrgId">The organisation.</param>
/// <param name="OrgName">Its name.</param>
/// <param name="Roles">The roles there.</param>
public sealed record OrgLeaverPlace(Guid AppId, string AppName, Guid OrgId, string OrgName, IReadOnlyList<string> Roles);

/// <summary>
/// rc.12 (G1, G2): the organisation console, for customer and branch admins (and application admins). Everything is scoped
/// to the trees the person administers; nothing outside them is ever shown.
/// </summary>
public interface IOrgConsoleService
{
    /// <summary>Every tree the person administers, across applications.</summary>
    /// <param name="userId">The person.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The trees.</returns>
    Task<IReadOnlyList<OrgScope>> ScopesAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>The overview of a tree, or <see langword="null"/> when the person does not administer it.</summary>
    /// <param name="userId">The admin.</param>
    /// <param name="appId">The application.</param>
    /// <param name="orgId">The organisation.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The overview.</returns>
    Task<OrgOverview?> OverviewAsync(Guid userId, Guid appId, Guid orgId, CancellationToken cancellationToken = default);

    /// <summary>The organisations of a tree, with people counts.</summary>
    /// <param name="userId">The admin.</param>
    /// <param name="appId">The application.</param>
    /// <param name="orgId">The tree's top.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The organisations, or <see langword="null"/>.</returns>
    Task<IReadOnlyList<OrgNode>?> TreeAsync(Guid userId, Guid appId, Guid orgId, CancellationToken cancellationToken = default);

    /// <summary>The people at one organisation (recorded as a read, as every list of people is).</summary>
    /// <param name="userId">The admin.</param>
    /// <param name="appId">The application.</param>
    /// <param name="orgId">The organisation.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The people, or <see langword="null"/>.</returns>
    Task<IReadOnlyList<OrgPerson>?> PeopleAsync(Guid userId, Guid appId, Guid orgId, CancellationToken cancellationToken = default);

    /// <summary>The roles the application offers at an organisation; empty when the customer keeps roles in the application.</summary>
    /// <param name="userId">The admin.</param>
    /// <param name="appId">The application.</param>
    /// <param name="orgId">The organisation.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The roles.</returns>
    Task<IReadOnlyList<OrgRoleChoice>> RolesAsync(Guid userId, Guid appId, Guid orgId, CancellationToken cancellationToken = default);

    /// <summary>Ends a person's place at one organisation, now or on a day, with a reason.</summary>
    /// <param name="userId">The admin.</param>
    /// <param name="appId">The application.</param>
    /// <param name="orgId">The organisation.</param>
    /// <param name="personId">The person.</param>
    /// <param name="endsOn">The last day (India time), or now.</param>
    /// <param name="reason">Why; the person reads it.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The result.</returns>
    Task<PartnerResult> EndAsync(Guid userId, Guid appId, Guid orgId, Guid personId, DateOnly? endsOn, string reason, CancellationToken cancellationToken = default);

    /// <summary>People in the admin's trees whose name or email starts with the text, for the leaver form.</summary>
    /// <param name="userId">The admin.</param>
    /// <param name="text">At least two characters.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Up to 10 people, with their places.</returns>
    Task<IReadOnlyList<OrgLeaver>> FindAsync(Guid userId, string text, CancellationToken cancellationToken = default);

    /// <summary>
    /// A leaver (G2): every place the person has in every tree the admin administers ends on one day (or now), with one
    /// reason. Each application is told through its usual events.
    /// </summary>
    /// <param name="userId">The admin.</param>
    /// <param name="personId">The person leaving.</param>
    /// <param name="lastDay">The last working day (India time), or now.</param>
    /// <param name="reason">Why; the person reads it.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>How many places end.</returns>
    Task<int> LeaverAsync(Guid userId, Guid personId, DateOnly? lastDay, string reason, CancellationToken cancellationToken = default);

    /// <summary>Gives a person in the tree an admin right (admin, or emergency access reviewer) at an organisation.</summary>
    /// <param name="userId">The admin giving it (verified with DigiLocker).</param>
    /// <param name="appId">The application.</param>
    /// <param name="orgId">The organisation.</param>
    /// <param name="personId">The person, who must have a place in the tree.</param>
    /// <param name="reviewer"><see langword="true"/> for the emergency access reviewer right, else admin.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The result.</returns>
    Task<PartnerResult> GrantAsync(Guid userId, Guid appId, Guid orgId, Guid personId, bool reviewer, CancellationToken cancellationToken = default);

    /// <summary>Ends a person's admin right at an organisation.</summary>
    /// <param name="userId">The admin.</param>
    /// <param name="appId">The application.</param>
    /// <param name="orgId">The organisation.</param>
    /// <param name="personId">The person.</param>
    /// <param name="reviewer">Which right.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The result.</returns>
    Task<PartnerResult> RevokeAsync(Guid userId, Guid appId, Guid orgId, Guid personId, bool reviewer, CancellationToken cancellationToken = default);

    /// <summary>Whether the customer keeps its roles in Sangam (a customer admin, or an application admin, decides).</summary>
    /// <param name="userId">The admin.</param>
    /// <param name="appId">The application.</param>
    /// <param name="orgId">The customer.</param>
    /// <param name="kept">The choice.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The result.</returns>
    Task<PartnerResult> SetRolesKeptAsync(Guid userId, Guid appId, Guid orgId, bool kept, CancellationToken cancellationToken = default);

    /// <summary>Switches emergency access on or off for an organisation (off by default; M12).</summary>
    /// <param name="userId">The admin.</param>
    /// <param name="appId">The application.</param>
    /// <param name="orgId">The organisation.</param>
    /// <param name="on">The choice.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The result.</returns>
    Task<PartnerResult> SetEmergencyAccessAsync(Guid userId, Guid appId, Guid orgId, bool on, CancellationToken cancellationToken = default);

    /// <summary>
    /// Closes a customer that leaves the application (an application admin only): every place and admin right under it ends
    /// at once, the people are told, and the accounts stay theirs.
    /// </summary>
    /// <param name="userId">The application admin.</param>
    /// <param name="appId">The application.</param>
    /// <param name="orgId">The customer.</param>
    /// <param name="reason">Why.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The result.</returns>
    Task<PartnerResult> CloseCustomerAsync(Guid userId, Guid appId, Guid orgId, string reason, CancellationToken cancellationToken = default);
}

/// <summary>rc.12 (A6): remembers which organisation a person last worked at in an application.</summary>
public interface IOrgChoiceStore
{
    /// <summary>The organisation the person last chose in the application, if any.</summary>
    /// <param name="userId">The person.</param>
    /// <param name="appId">The application.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The organisation, or <see langword="null"/>.</returns>
    Task<Guid?> LastAsync(Guid userId, Guid appId, CancellationToken cancellationToken = default);

    /// <summary>Records the choice.</summary>
    /// <param name="userId">The person.</param>
    /// <param name="appId">The application.</param>
    /// <param name="orgId">The organisation chosen.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task.</returns>
    Task RememberAsync(Guid userId, Guid appId, Guid orgId, CancellationToken cancellationToken = default);
}
