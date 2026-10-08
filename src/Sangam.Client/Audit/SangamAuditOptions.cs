namespace Sangam.Client.Audit;

/// <summary>Settings for the shared audit helper (SGM-208 §7).</summary>
public sealed class SangamAuditOptions
{
    /// <summary>Configuration section.</summary>
    public const string SectionName = "Sangam:Audit";

    /// <summary>Your application's Sangam client id (<c>source.app_id</c>).</summary>
    public string AppId { get; set; } = string.Empty;

    /// <summary>Your application's version (<c>source.app_version</c>).</summary>
    public string AppVersion { get; set; } = "0.0.0";

    /// <summary><c>production</c>, <c>staging</c> or <c>development</c>.</summary>
    public string Environment { get; set; } = "production";

    /// <summary>Where events wait until the audit service takes them (a durable JSON Lines file).</summary>
    public string BufferPath { get; set; } = "sangam-audit/pending.jsonl";

    /// <summary>The audit service's <c>POST /v1/events</c> address; empty until the service exists, and events stay buffered.</summary>
    public string? Endpoint { get; set; }
}
