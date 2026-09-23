using Sangam.Identity.Domain.Enums;

namespace Sangam.Identity.Application.Abstractions;

/// <summary>What a use case reports to the audit log. The writer adds the timestamp.</summary>
/// <param name="Action">Action name from <see cref="Domain.AuditActions"/>.</param>
/// <param name="ActorType">Capacity in which the actor acted.</param>
/// <param name="ActorUserId">The human, when known.</param>
/// <param name="ActorAppId">App context, when any.</param>
/// <param name="TargetType">Kind of thing acted upon.</param>
/// <param name="TargetId">Id of the thing acted upon.</param>
/// <param name="Metadata">JSON object with event detail; never secrets. Defaults to <c>{}</c>.</param>
/// <param name="IpAddress">Client IP, when known.</param>
/// <param name="UserAgent">Client user agent, when known.</param>
public sealed record AuditEntry(
    string Action,
    AuditActorType ActorType,
    Guid? ActorUserId = null,
    Guid? ActorAppId = null,
    string? TargetType = null,
    Guid? TargetId = null,
    string Metadata = "{}",
    string? IpAddress = null,
    string? UserAgent = null);
