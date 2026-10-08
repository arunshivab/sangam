namespace Sangam.Identity.Domain.Entities;

/// <summary>
/// A custom user attribute an application defines (PR-25, CAP-007, SGM-209 §6): for the whole application, or for one
/// organisation (a tenant). Never health data: the definition is refused when it looks like it, and its maker declares it
/// is not.
/// </summary>
public class UserAttributeDefinition
{
    /// <summary>Definition id.</summary>
    public Guid Id { get; set; }

    /// <summary>The application.</summary>
    public Guid AppId { get; set; }

    /// <summary>The organisation it applies to, or null for the whole application.</summary>
    public Guid? OrgId { get; set; }

    /// <summary>Its key (lowercase letters, digits and underscores), for example <c>employee_number</c>.</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>Its label, as people see it.</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary><c>text</c>, <c>number</c>, <c>date</c>, <c>boolean</c> or <c>choice</c>.</summary>
    public string Type { get; set; } = "text";

    /// <summary>For <c>choice</c>: the allowed values, comma-separated.</summary>
    public string? Choices { get; set; }

    /// <summary><c>admin</c> (the application's administrators) or <c>person</c> (the person, in the account portal, as well).</summary>
    public string EditableBy { get; set; } = "admin";

    /// <summary>Created.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Who created it.</summary>
    public Guid? CreatedByUserId { get; set; }

    /// <summary>When it was retired (its values are kept but no longer shown or released).</summary>
    public DateTimeOffset? RetiredAt { get; set; }
}

/// <summary>One person's value of one custom attribute.</summary>
public class UserAttributeValue
{
    /// <summary>The definition.</summary>
    public Guid DefinitionId { get; set; }

    /// <summary>The person.</summary>
    public Guid UserId { get; set; }

    /// <summary>The value, as text in the definition's type.</summary>
    public string Value { get; set; } = string.Empty;

    /// <summary>Last set.</summary>
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Who set it (the person, an administrator, or null for the application through the API).</summary>
    public Guid? UpdatedByUserId { get; set; }
}

/// <summary>
/// A custom claim an application asks for (PR-25, CAP-053): a name in its tokens, filled from one of its attributes or
/// from the person's memberships. Released only under the <c>attributes</c> scope the person consented to.
/// </summary>
public class AppClaimMapping
{
    /// <summary>Mapping id.</summary>
    public Guid Id { get; set; }

    /// <summary>The application.</summary>
    public Guid AppId { get; set; }

    /// <summary>The claim's name in tokens and userinfo.</summary>
    public string ClaimName { get; set; } = string.Empty;

    /// <summary><c>attribute</c>, <c>roles</c>, <c>permissions</c> or <c>org_names</c>.</summary>
    public string Source { get; set; } = "attribute";

    /// <summary>For <c>attribute</c>: the attribute's key.</summary>
    public string? AttributeKey { get; set; }

    /// <summary>Created.</summary>
    public DateTimeOffset CreatedAt { get; set; }
}
