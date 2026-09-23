using Sangam.Identity.Domain.Enums;

namespace Sangam.Identity.Domain.Entities;

/// <summary>
/// Append-only compliance record. Database rules block UPDATE and DELETE; retention is five
/// years for healthcare-related events and one year otherwise.
/// </summary>
public sealed class AuditEvent
{
    /// <summary>Monotonic primary key.</summary>
    public long Id { get; set; }

    /// <summary>The human behind the event, when there is one.</summary>
    public Guid? ActorUserId { get; set; }

    /// <summary>In what capacity the actor acted.</summary>
    public AuditActorType ActorType { get; set; } = AuditActorType.User;

    /// <summary>App context for <see cref="AuditActorType.Api"/> events and user events that happened inside an app.</summary>
    public Guid? ActorAppId { get; set; }

    /// <summary>Dotted action name from the taxonomy ("user.login.success", "org_membership.grant").</summary>
    public string Action { get; set; } = string.Empty;

    /// <summary>Kind of thing acted upon ("user", "organisation", "app", "consent", "role").</summary>
    public string? TargetType { get; set; }

    /// <summary>Id of the thing acted upon.</summary>
    public Guid? TargetId { get; set; }

    /// <summary>JSON with event-specific detail; never contains secrets.</summary>
    public string Metadata { get; set; } = "{}";

    /// <summary>Client IP, when known.</summary>
    public string? IpAddress { get; set; }

    /// <summary>Client user agent, when known.</summary>
    public string? UserAgent { get; set; }

    /// <summary>When it happened (UTC).</summary>
    public DateTimeOffset OccurredAt { get; set; }
}
