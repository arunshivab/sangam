using System.Text.Json.Serialization;
using Sangam.Identity.Domain.Enums;

namespace Sangam.Identity.Application.Tenancy;

/// <summary>A role as exposed on the management API.</summary>
/// <param name="Code">Role code.</param>
/// <param name="DisplayName">Display name.</param>
/// <param name="Description">Description.</param>
/// <param name="Permissions">Permission strings.</param>
/// <param name="OrgId">Restricting organisation, or <see langword="null"/> for app-wide.</param>
/// <param name="IsSystem">Whether the role is the app's built-in <c>org_admin</c>.</param>
/// <param name="RetiredAt">When retired, or <see langword="null"/>.</param>
public sealed record RoleDto(string Code, string DisplayName, string? Description, IReadOnlyList<string> Permissions, Guid? OrgId, bool IsSystem, DateTimeOffset? RetiredAt);

/// <summary>Input for creating or updating a role.</summary>
/// <param name="DisplayName">Display name.</param>
/// <param name="Description">Description.</param>
/// <param name="Permissions">Permission strings (opaque to Sangam).</param>
/// <param name="OrgId">Restrict to one organisation, or <see langword="null"/> for app-wide.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RoleUpsert(string DisplayName, string? Description, IReadOnlyList<string> Permissions, Guid? OrgId);

/// <summary>An organisation as exposed on the management API.</summary>
/// <param name="Id">Organisation id.</param>
/// <param name="Name">Display name.</param>
/// <param name="Type">Type code.</param>
/// <param name="ParentId">Parent id, or <see langword="null"/> for a root.</param>
/// <param name="Path">Materialised path.</param>
/// <param name="Depth">Depth (0 = root).</param>
/// <param name="Status">Lifecycle state.</param>
/// <param name="Metadata">Opaque JSON the app attached.</param>
public sealed record OrganisationDto(Guid Id, string Name, string Type, Guid? ParentId, string Path, int Depth, OrganisationStatus Status, string Metadata);

/// <summary>Input for creating or updating an organisation. Parent and type are fixed after creation.</summary>
/// <param name="Name">Display name.</param>
/// <param name="Type">Type code (from <c>org_types</c>).</param>
/// <param name="ParentId">Parent organisation, or <see langword="null"/> for a root.</param>
/// <param name="Metadata">Opaque JSON object; <see langword="null"/> keeps the existing value.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record OrganisationUpsert(string Name, string Type, Guid? ParentId, string? Metadata);

/// <summary>A person's place in an organisation as exposed on the management API.</summary>
/// <param name="UserId">The member.</param>
/// <param name="OrgId">The organisation.</param>
/// <param name="Role">The first of <paramref name="Roles"/> (kept for clients written before rc.12), or null.</param>
/// <param name="AppliesToDescendants">Always false since rc.12: a membership is in its own organisation only. Kept so
/// existing clients do not break.</param>
/// <param name="GrantedAt">When granted.</param>
/// <param name="ExpiresAt">When a time-limited membership ends (PR-25), or null.</param>
/// <param name="Roles">rc.12: every role the person holds there (several allowed); empty for staff whose roles the
/// application keeps.</param>
public sealed record MembershipDto(Guid UserId, Guid OrgId, string? Role, bool AppliesToDescendants, DateTimeOffset GrantedAt, DateTimeOffset? ExpiresAt = null, IReadOnlyList<string>? Roles = null);

/// <summary>Input for placing a person in an organisation, or changing their roles there.</summary>
/// <param name="Role">One role code in the app's vocabulary (kept for clients written before rc.12); ignored when
/// <paramref name="Roles"/> is given.</param>
/// <param name="AppliesToDescendants">Must be false since rc.12 (decision of 14 Oct 2026): add the person to each
/// organisation instead.</param>
/// <param name="ExpiresAt">
/// PR-25: when the roles end by themselves (a contractor, an auditor); null for ones that last until taken away. Must be
/// in the future and at most five years away.
/// </param>
/// <param name="Roles">rc.12: the complete set of role codes the person holds there; empty (or both this and
/// <paramref name="Role"/> absent) for staff whose roles the application keeps.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record MembershipUpsert(string? Role = null, bool AppliesToDescendants = false, DateTimeOffset? ExpiresAt = null, IReadOnlyList<string>? Roles = null)
{
    /// <summary>The role codes asked for: <see cref="Roles"/>, else <see cref="Role"/>, else none.</summary>
    public IReadOnlyList<string> RoleCodes => Roles ?? (string.IsNullOrWhiteSpace(Role) ? [] : [Role]);
}

/// <summary>
/// An invitation the management API sent (rc.5). Since rc.12 new invitations are refused (410 Gone): people are added with
/// <see cref="StaffAddRequest"/>, under an admin's own identity.
/// </summary>
/// <param name="Email">The address to invite.</param>
/// <param name="Role">Role code in the app's vocabulary.</param>
/// <param name="AppliesToDescendants">Whether the role flows down the subtree.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record InvitationRequest(string Email, string Role, bool AppliesToDescendants = false);

/// <summary>rc.12: a person's details, for the check before adding them (<c>POST /orgs/{orgId}/staff/check</c>).</summary>
/// <param name="FirstName">First name.</param>
/// <param name="LastName">Last name.</param>
/// <param name="Email">Email.</param>
/// <param name="Mobile">Mobile in E.164 form, if known.</param>
/// <param name="DateOfBirth">Date of birth (<c>yyyy-mm-dd</c>).</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record StaffDetailsRequest(string FirstName, string LastName, string Email, string? Mobile, DateOnly DateOfBirth)
{
    /// <summary>As the staff service takes it.</summary>
    /// <returns>The details.</returns>
    public StaffDetails ToDetails() => new(FirstName ?? string.Empty, LastName ?? string.Empty, Email ?? string.Empty, Mobile, DateOfBirth);
}

/// <summary>rc.12: one person added to one organisation (<c>POST /orgs/{orgId}/staff</c>). Send exactly one of
/// <paramref name="Person"/>, <paramref name="JoinCode"/>, <paramref name="StaffRef"/> and <paramref name="UserId"/>.</summary>
/// <param name="Person">The person's details (push: an activation code for the admin to hand over).</param>
/// <param name="JoinCode">The join code the person made in their Sangam portal (pull).</param>
/// <param name="StaffRef">The <c>staffRef</c> an earlier call returned, for this person's other organisations.</param>
/// <param name="UserId">Someone already in this customer.</param>
/// <param name="Roles">Role codes at this organisation; none for staff whose roles the application keeps.</param>
/// <param name="StartsOn">The first day of access (India time), if later.</param>
/// <param name="ExpiresAt">When the roles end, if they do.</param>
/// <param name="Confirmed">The admin saw the check's warning and proceeds.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record StaffAddRequest(
    StaffDetailsRequest? Person = null,
    string? JoinCode = null,
    Guid? StaffRef = null,
    Guid? UserId = null,
    IReadOnlyList<string>? Roles = null,
    DateOnly? StartsOn = null,
    DateTimeOffset? ExpiresAt = null,
    bool Confirmed = false)
{
    /// <summary>As the staff service takes it.</summary>
    /// <returns>The addition.</returns>
    public StaffAddition ToAddition() => new(Person?.ToDetails(), JoinCode, StaffRef, UserId, Roles, StartsOn, ExpiresAt, Confirmed);
}

/// <summary>Outcome of a management call.</summary>
public enum ManagementStatus
{
    /// <summary>Done.</summary>
    Ok = 0,

    /// <summary>Referenced organisation, role, user or type does not exist (or belongs to another app).</summary>
    NotFound = 1,

    /// <summary>The input breaks a rule (message says which).</summary>
    Invalid = 2,
}

/// <summary>Result of a management call.</summary>
/// <typeparam name="T">Payload type.</typeparam>
/// <param name="Status">Outcome.</param>
/// <param name="Value">Payload on <see cref="ManagementStatus.Ok"/>.</param>
/// <param name="Message">Explanation on failure.</param>
public sealed record ManagementResult<T>(ManagementStatus Status, T? Value, string? Message);

/// <summary>Factory helpers for <see cref="ManagementResult{T}"/>.</summary>
public static class ManagementResult
{
    /// <summary>A successful result.</summary>
    public static ManagementResult<T> Ok<T>(T value) => new(ManagementStatus.Ok, value, null);

    /// <summary>A not-found result.</summary>
    public static ManagementResult<T> NotFound<T>(string message) => new(ManagementStatus.NotFound, default, message);

    /// <summary>An invalid-input result.</summary>
    public static ManagementResult<T> Invalid<T>(string message) => new(ManagementStatus.Invalid, default, message);
}

/// <summary>
/// Who is making a management change, so the audit trail names them. Required on every change:
/// the same service serves an application's own code (<see cref="Api"/>) and a partner's staff on
/// the partner console (<see cref="AppAdmin"/>), and a human must never be recorded as a machine.
/// </summary>
/// <param name="Type">How the actor is recorded.</param>
/// <param name="UserId">The person, when a person is acting.</param>
public sealed record ManagementActor(AuditActorType Type, Guid? UserId)
{
    /// <summary>An application's own backend, using its client credentials.</summary>
    public static ManagementActor Api { get; } = new(AuditActorType.Api, null);

    /// <summary>
    /// rc.13 (decision of 14 Oct 2026): an application's backend acting for one of its admins, named by their own access
    /// token, so every change to a person's place records who asked.
    /// </summary>
    /// <param name="adminUserId">The admin's Sangam user id.</param>
    public static ManagementActor ApiFor(Guid adminUserId) => new(AuditActorType.Api, adminUserId);

    /// <summary>A partner's staff member acting on the partner console.</summary>
    /// <param name="userId">Their Sangam user id.</param>
    public static ManagementActor AppAdmin(Guid userId) => new(AuditActorType.Admin, userId);

    /// <summary>rc.10 (P4): the person themselves, ending their own roles from their portal.</summary>
    /// <param name="userId">Their Sangam user id.</param>
    public static ManagementActor Self(Guid userId) => new(AuditActorType.User, userId);

    /// <summary>The person themselves, accepting an invitation the application sent through the API (rc.5).</summary>
    /// <param name="userId">Their Sangam user id.</param>
    public static ManagementActor Person(Guid userId) => new(AuditActorType.User, userId);

    /// <summary>
    /// rc.11 (R3): why a partner's administrator ended or shortened someone's role. Recorded in the audit trail and shown
    /// to the person in their Activity; never sent to the application.
    /// </summary>
    public string? Reason { get; init; }
}
