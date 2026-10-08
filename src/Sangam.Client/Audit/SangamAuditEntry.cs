namespace Sangam.Client.Audit;

/// <summary>What an application says happened; the helper adds who, where and from which device (SGM-208 §7).</summary>
/// <param name="Action">domain.object.verb, for example <c>qms.document.approve</c>.</param>
/// <param name="Category">access, create, update, delete, approve, sign, export, admin or security.</param>
/// <param name="TargetType">What was acted on.</param>
/// <param name="TargetId">Its identifier in your application.</param>
/// <param name="Outcome">success, failure or denied.</param>
public sealed record SangamAuditEntry(string Action, string Category, string TargetType, string TargetId, string Outcome = "success")
{
    /// <summary>A human-readable label for the target, with no personal data beyond what is necessary.</summary>
    public string? TargetDisplay { get; init; }

    /// <summary>The organisation in which it happened.</summary>
    public Guid? OrganisationId { get; init; }

    /// <summary>That organisation's path (from <c>sangam_orgs</c>).</summary>
    public string? OrganisationPath { get; init; }

    /// <summary>public, internal, personal, sensitive-personal or health.</summary>
    public string DataClassification { get; init; } = "internal";

    /// <summary>A typed reason, for example an override justification.</summary>
    public string? Reason { get; init; }

    /// <summary>For a signature: the signature token id, its meaning and the record hash (SGM-207).</summary>
    public (string TokenId, string Meaning, string RecordHash)? Signature { get; init; }

    /// <summary>Field-level changes.</summary>
    public IReadOnlyList<SangamAuditChange> Changes { get; init; } = [];

    /// <summary>Fields whose values are masked in <see cref="Changes"/>.</summary>
    public IReadOnlyCollection<string> Sensitive { get; init; } = [];

    /// <summary>
    /// Who acted when it was not the signed-in person: <c>system</c> (a job), <c>api</c> (another application, with
    /// <see cref="ClientId"/>) or <c>anonymous</c>. Leave empty for the signed-in user.
    /// </summary>
    public string? ActorType { get; init; }

    /// <summary>For an <c>api</c> actor: the calling application's client id.</summary>
    public string? ClientId { get; init; }

    /// <summary>A trace id to join with logs.</summary>
    public string? CorrelationId { get; init; }
}

/// <summary>One field's change.</summary>
/// <param name="Field">The field.</param>
/// <param name="Before">Its value before.</param>
/// <param name="After">Its value after.</param>
public sealed record SangamAuditChange(string Field, object? Before, object? After);
