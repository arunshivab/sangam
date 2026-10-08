using Sangam.Identity.Application.Partners;

namespace Sangam.Identity.Application.Attributes;

/// <summary>
/// Custom user attributes and custom claims (PR-25, CAP-007 and CAP-053, SGM-209 §6): what an application keeps about
/// people beyond Sangam's own profile, who may change it, and which of it — or of the person's memberships — reaches the
/// application's tokens under the <c>attributes</c> scope.
/// </summary>
public interface IAttributeService
{
    /// <summary>The application's attributes and claims, or <see langword="null"/> for someone who does not administer it.</summary>
    /// <param name="userId">The administrator.</param>
    /// <param name="appId">The application.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<AttributesView?> GetAsync(Guid userId, Guid appId, CancellationToken cancellationToken = default);

    /// <summary>Defines an attribute.</summary>
    /// <param name="userId">The administrator.</param>
    /// <param name="appId">The application.</param>
    /// <param name="input">The definition.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<PartnerResult> DefineAsync(Guid userId, Guid appId, AttributeDefinitionInput input, CancellationToken cancellationToken = default);

    /// <summary>Retires an attribute: its values stay but are no longer shown or released.</summary>
    /// <param name="userId">The administrator.</param>
    /// <param name="appId">The application.</param>
    /// <param name="definitionId">The attribute.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<PartnerResult> RetireAsync(Guid userId, Guid appId, Guid definitionId, CancellationToken cancellationToken = default);

    /// <summary>Adds a custom claim.</summary>
    /// <param name="userId">The administrator.</param>
    /// <param name="appId">The application.</param>
    /// <param name="input">The claim.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<PartnerResult> AddClaimAsync(Guid userId, Guid appId, ClaimMappingInput input, CancellationToken cancellationToken = default);

    /// <summary>Removes a custom claim.</summary>
    /// <param name="userId">The administrator.</param>
    /// <param name="appId">The application.</param>
    /// <param name="mappingId">The claim.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<PartnerResult> RemoveClaimAsync(Guid userId, Guid appId, Guid mappingId, CancellationToken cancellationToken = default);

    /// <summary>A linked person's values, for an administrator (or the application itself); null when not allowed.</summary>
    /// <param name="userId">The administrator, or null for the application itself through the management API.</param>
    /// <param name="appId">The application.</param>
    /// <param name="personId">The person.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IReadOnlyList<AttributeValueRow>?> GetValuesAsync(Guid? userId, Guid appId, Guid personId, CancellationToken cancellationToken = default);

    /// <summary>Sets a linked person's values (keys absent are left alone; an empty value clears one).</summary>
    /// <param name="userId">The administrator, or null for the application itself through the management API.</param>
    /// <param name="appId">The application.</param>
    /// <param name="personId">The person.</param>
    /// <param name="values">Key to value.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<PartnerResult> SetValuesAsync(Guid? userId, Guid appId, Guid personId, IReadOnlyDictionary<string, string?> values, CancellationToken cancellationToken = default);

    /// <summary>What each application the person has linked keeps about them, for the account portal; rows marked <c>person</c> are theirs to change.</summary>
    /// <param name="personId">The person.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IReadOnlyList<PersonAttributeGroup>> GetMineAsync(Guid personId, CancellationToken cancellationToken = default);

    /// <summary>Sets one of the person's own values (only an attribute marked as theirs to change).</summary>
    /// <param name="personId">The person.</param>
    /// <param name="appId">The application.</param>
    /// <param name="values">Key to value.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<PartnerResult> SetMineAsync(Guid personId, Guid appId, IReadOnlyDictionary<string, string?> values, CancellationToken cancellationToken = default);

    /// <summary>The application's custom claims for the person, as token claims (name to string, number, boolean or list).</summary>
    /// <param name="personId">The person.</param>
    /// <param name="appId">The application.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IReadOnlyDictionary<string, object>> ClaimsAsync(Guid personId, Guid appId, CancellationToken cancellationToken = default);
}

/// <summary>An attribute definition as entered.</summary>
/// <param name="Key">Key: lowercase letters, digits and underscores.</param>
/// <param name="Label">Label people see.</param>
/// <param name="Type">text, number, date, boolean or choice.</param>
/// <param name="Choices">For choice: the values, comma-separated.</param>
/// <param name="EditableBy">admin or person.</param>
/// <param name="OrgId">One organisation, or null for the whole application.</param>
/// <param name="NotHealthData">The maker's declaration that it is not health data.</param>
public sealed record AttributeDefinitionInput(string Key, string Label, string Type, string? Choices, string EditableBy, Guid? OrgId, bool NotHealthData);

/// <summary>A custom claim as entered.</summary>
/// <param name="ClaimName">The claim's name.</param>
/// <param name="Source">attribute, roles, permissions or org_names.</param>
/// <param name="AttributeKey">For attribute: its key.</param>
public sealed record ClaimMappingInput(string ClaimName, string Source, string? AttributeKey);

/// <summary>The application's attributes and claims.</summary>
/// <param name="Definitions">Live attributes.</param>
/// <param name="Claims">Custom claims.</param>
/// <param name="Organisations">The organisations an attribute may be limited to.</param>
public sealed record AttributesView(IReadOnlyList<AttributeDefinitionView> Definitions, IReadOnlyList<ClaimMappingView> Claims, IReadOnlyList<(Guid Id, string Name)> Organisations);

/// <summary>An attribute as shown.</summary>
/// <param name="Id">Id.</param>
/// <param name="Key">Key.</param>
/// <param name="Label">Label.</param>
/// <param name="Type">Type.</param>
/// <param name="Choices">Choices.</param>
/// <param name="EditableBy">admin or person.</param>
/// <param name="OrgId">Organisation, or null.</param>
/// <param name="OrgName">Its name.</param>
/// <param name="Values">How many people have a value.</param>
public sealed record AttributeDefinitionView(Guid Id, string Key, string Label, string Type, string? Choices, string EditableBy, Guid? OrgId, string? OrgName, int Values);

/// <summary>A custom claim as shown.</summary>
/// <param name="Id">Id.</param>
/// <param name="ClaimName">Name.</param>
/// <param name="Source">Source.</param>
/// <param name="AttributeKey">Attribute key, if any.</param>
public sealed record ClaimMappingView(Guid Id, string ClaimName, string Source, string? AttributeKey);

/// <summary>One attribute and a person's value.</summary>
/// <param name="Key">Key.</param>
/// <param name="Label">Label.</param>
/// <param name="Type">Type.</param>
/// <param name="Choices">Choices.</param>
/// <param name="EditableBy">admin or person.</param>
/// <param name="Value">The value, or null.</param>
public sealed record AttributeValueRow(string Key, string Label, string Type, string? Choices, string EditableBy, string? Value);

/// <summary>A person's own attributes for one application.</summary>
/// <param name="AppId">The application.</param>
/// <param name="AppName">Its name.</param>
/// <param name="Values">The attributes they may change, with values.</param>
public sealed record PersonAttributeGroup(Guid AppId, string AppName, IReadOnlyList<AttributeValueRow> Values);
