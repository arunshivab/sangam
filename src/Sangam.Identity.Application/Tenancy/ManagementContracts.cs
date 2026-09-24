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
public sealed record OrganisationUpsert(string Name, string Type, Guid? ParentId, string? Metadata);

/// <summary>A membership as exposed on the management API.</summary>
/// <param name="UserId">The member.</param>
/// <param name="OrgId">The organisation.</param>
/// <param name="Role">Role code.</param>
/// <param name="AppliesToDescendants">Whether the role flows down the subtree.</param>
/// <param name="GrantedAt">When granted.</param>
public sealed record MembershipDto(Guid UserId, Guid OrgId, string Role, bool AppliesToDescendants, DateTimeOffset GrantedAt);

/// <summary>Input for granting or changing a membership.</summary>
/// <param name="Role">Role code in the app's vocabulary.</param>
/// <param name="AppliesToDescendants">Whether the role flows down the subtree.</param>
public sealed record MembershipUpsert(string Role, bool AppliesToDescendants);

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
