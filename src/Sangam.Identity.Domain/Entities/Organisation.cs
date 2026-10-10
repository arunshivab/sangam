using Sangam.Identity.Domain.Enums;

namespace Sangam.Identity.Domain.Entities;

/// <summary>
/// A tenant: corporate group, hospital, clinic, laboratory or department. Organisations form a
/// tree through <see cref="ParentOrgId"/>; <see cref="Path"/> is the materialised ancestor chain
/// (<c>/{root-id}/{child-id}/…/{this-id}/</c>) so "everything under X" is a prefix query.
/// </summary>
public sealed class Organisation
{
    /// <summary>Primary key.</summary>
    public Guid Id { get; set; }

    /// <summary>Display name. Not unique — two clinics may share a name in different cities.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Foreign key to <see cref="OrgType.Code"/>.</summary>
    public string OrgTypeCode { get; set; } = string.Empty;

    /// <summary>Navigation to the organisation type.</summary>
    public OrgType? OrgType { get; set; }

    /// <summary>Parent organisation, or <see langword="null"/> for a root.</summary>
    public Guid? ParentOrgId { get; set; }

    /// <summary>Navigation to the parent.</summary>
    public Organisation? Parent { get; set; }

    /// <summary>Materialised path of ancestor ids ending with this id, slash-delimited.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>Depth in the tree: 0 for a root.</summary>
    public int Depth { get; set; }

    /// <summary>The app through which this organisation was first registered (data ownership on partner exit).</summary>
    public Guid RegisteredViaAppId { get; set; }

    /// <summary>Navigation to the registering app.</summary>
    public App? RegisteredViaApp { get; set; }

    /// <summary>Free-form JSON the registering app may attach (address, registration number). Opaque to Sangam.</summary>
    public string Metadata { get; set; } = "{}";

    /// <summary>
    /// The organisation's sign-in rule for its members, inherited by everything below it; <see langword="null"/>
    /// inherits. Only stricter than the application's (PR-16, SGM-209 §7).
    /// </summary>
    public SignInPolicy? SignInPolicy { get; set; }

    /// <summary>Shortest password for its members; <see langword="null"/> inherits.</summary>
    public int? MinPasswordLength { get; set; }

    /// <summary>Second-factor rule for its members; <see langword="null"/> inherits.</summary>
    public MfaRequirement? MfaRequirement { get; set; }

    /// <summary>Breached-password check for its members; <see langword="null"/> inherits, and only <see langword="true"/> is stricter.</summary>
    public bool? BreachedPasswordCheck { get; set; }

    /// <summary>Character-type rule for its members (rc.5); <see langword="null"/> inherits, and only <see langword="true"/> is stricter.</summary>
    public bool? RequireCharacterTypes { get; set; }

    /// <summary>Lifecycle state.</summary>
    public OrganisationStatus Status { get; set; } = OrganisationStatus.Active;

    /// <summary>When the organisation was created (UTC).</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>When any column last changed (UTC).</summary>
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>When deletion was requested (UTC).</summary>
    public DateTimeOffset? DeletedAt { get; set; }
}
