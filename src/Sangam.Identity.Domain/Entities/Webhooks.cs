namespace Sangam.Identity.Domain.Entities;

/// <summary>
/// An address an application gave for event notifications (PR-24, SGM-217 §5): the events it wants, its signing
/// secret (encrypted), and whether deliveries to it are failing.
/// </summary>
public class WebhookEndpoint
{
    /// <summary>Endpoint id.</summary>
    public Guid Id { get; set; }

    /// <summary>The application.</summary>
    public Guid AppId { get; set; }

    /// <summary>The https address Sangam posts to.</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>What it is for, in the application's words.</summary>
    public string? Description { get; set; }

    /// <summary>The event types it receives, comma-separated (<see cref="AppEventTypes"/>).</summary>
    public string Events { get; set; } = string.Empty;

    /// <summary>The signing secret (<c>whsec_…</c>), encrypted with the data-protection key ring.</summary>
    public string ProtectedSecret { get; set; } = string.Empty;

    /// <summary>The secret before the last rotation, still signing until <see cref="PreviousSecretExpiresAt"/>.</summary>
    public string? ProtectedPreviousSecret { get; set; }

    /// <summary>When the previous secret stops signing.</summary>
    public DateTimeOffset? PreviousSecretExpiresAt { get; set; }

    /// <summary>Whether deliveries are made.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary><c>ok</c> or <c>failing</c> (a delivery gave up after its retries).</summary>
    public string Status { get; set; } = "ok";

    /// <summary>When a delivery last succeeded.</summary>
    public DateTimeOffset? LastSuccessAt { get; set; }

    /// <summary>When a delivery last failed.</summary>
    public DateTimeOffset? LastFailureAt { get; set; }

    /// <summary>Created.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Last changed.</summary>
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Who last changed it.</summary>
    public Guid? UpdatedByUserId { get; set; }
}

/// <summary>One event for one endpoint, with its attempts (SGM-217 §4: the delivery log).</summary>
public class WebhookDelivery
{
    /// <summary>Row number.</summary>
    public long Id { get; set; }

    /// <summary>The endpoint.</summary>
    public Guid EndpointId { get; set; }

    /// <summary>The application.</summary>
    public Guid AppId { get; set; }

    /// <summary>The event (also the <c>webhook-id</c> header, the same on every attempt and replay).</summary>
    public Guid EventId { get; set; }

    /// <summary>The event type.</summary>
    public string EventType { get; set; } = string.Empty;

    /// <summary>The body sent, exactly: ids and codes, no personal data.</summary>
    public string Payload { get; set; } = string.Empty;

    /// <summary><c>pending</c>, <c>done</c> or <c>dead</c>.</summary>
    public string Status { get; set; } = "pending";

    /// <summary>Attempts so far.</summary>
    public int Attempts { get; set; }

    /// <summary>When the next attempt is due.</summary>
    public DateTimeOffset NextAttemptAt { get; set; }

    /// <summary>Queued.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Finished.</summary>
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>The last HTTP status.</summary>
    public int? LastStatusCode { get; set; }

    /// <summary>How long the last attempt took.</summary>
    public int? LatencyMs { get; set; }

    /// <summary>The start of the receiver's answer.</summary>
    public string? ResponseSnippet { get; set; }

    /// <summary>What went wrong on the last attempt.</summary>
    public string? LastError { get; set; }
}
