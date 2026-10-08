namespace Sangam.Identity.Domain.Entities;

/// <summary>
/// Something that changed for an application (SGM-217 §2), written in the same database as the change itself (an
/// outbox): SCIM provisioning (PR-23, SGM-216) and signed webhooks (PR-24) are both fed from here. It carries ids, never
/// personal data. Written only for applications that have a consumer switched on.
/// </summary>
public class AppEvent
{
    /// <summary>Order of writing; events are handed out in this order.</summary>
    public long Sequence { get; set; }

    /// <summary>The event's id, as receivers see it (<c>evt_…</c>).</summary>
    public Guid Id { get; set; }

    /// <summary>The application concerned.</summary>
    public Guid AppId { get; set; }

    /// <summary>The event type (<see cref="AppEventTypes"/>).</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>The person concerned, if any.</summary>
    public Guid? UserId { get; set; }

    /// <summary>The organisation concerned, if any.</summary>
    public Guid? OrgId { get; set; }

    /// <summary>Event details as a JSON object: ids and codes only.</summary>
    public string Data { get; set; } = "{}";

    /// <summary>When it happened.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>When it was handed to the consumers; null until then.</summary>
    public DateTimeOffset? DispatchedAt { get; set; }
}

/// <summary>The event types (SGM-217 §2).</summary>
public static class AppEventTypes
{
    /// <summary>A person linked the application (consented to it, or was given a role in it).</summary>
    public const string UserCreated = "user.created";

    /// <summary>A person's profile changed (name, e-mail, mobile, verification).</summary>
    public const string UserUpdated = "user.updated";

    /// <summary>A person can no longer use the application: suspended, deleted, consent withdrawn or last role revoked.</summary>
    public const string UserDeactivated = "user.deactivated";

    /// <summary>A suspended or deleted account was restored.</summary>
    public const string UserReactivated = "user.reactivated";

    /// <summary>A role was given at an organisation.</summary>
    public const string MembershipGranted = "membership.granted";

    /// <summary>A role was taken away (or expired).</summary>
    public const string MembershipRevoked = "membership.revoked";

    /// <summary>A person's sessions were ended: the application should end its own.</summary>
    public const string SessionRevoked = "session.revoked";

    /// <summary>The person withdrew their consent to the application.</summary>
    public const string ConsentRevoked = "consent.revoked";

    /// <summary>A role was created, changed or retired.</summary>
    public const string RoleChanged = "role.changed";

    /// <summary>A test event sent from the partner console.</summary>
    public const string Ping = "ping";

    /// <summary>Every type an endpoint may subscribe to, in the order the console shows them.</summary>
    public static IReadOnlyList<string> All { get; } = [UserCreated, UserUpdated, UserDeactivated, UserReactivated, MembershipGranted, MembershipRevoked, SessionRevoked, ConsentRevoked, RoleChanged];
}

/// <summary>An application's SCIM 2.0 server, to which Sangam provisions people (PR-23, SGM-216).</summary>
public class ScimTarget
{
    /// <summary>The application (one target each).</summary>
    public Guid AppId { get; set; }

    /// <summary>The SCIM base address, for example <c>https://lims.example.in/scim/v2</c>.</summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary><c>bearer</c> (a token the application gave) or <c>sangam</c> (a short token Sangam signs for each call).</summary>
    public string AuthMode { get; set; } = "bearer";

    /// <summary>The application's bearer token, encrypted with the data-protection key ring.</summary>
    public string? ProtectedToken { get; set; }

    /// <summary><c>role</c>: one group per role; <c>role_org</c>: one group per role at each organisation.</summary>
    public string GroupMapping { get; set; } = "role";

    /// <summary>Whether a person who loses access is deleted (DELETE) instead of deactivated (active=false).</summary>
    public bool DeleteOnDeprovision { get; set; }

    /// <summary>Whether provisioning is on.</summary>
    public bool Enabled { get; set; }

    /// <summary><c>ok</c> or <c>failing</c> (a delivery gave up after its retries).</summary>
    public string Status { get; set; } = "ok";

    /// <summary>When a delivery last succeeded.</summary>
    public DateTimeOffset? LastSuccessAt { get; set; }

    /// <summary>When a delivery last failed.</summary>
    public DateTimeOffset? LastFailureAt { get; set; }

    /// <summary>When the last reconciliation ran.</summary>
    public DateTimeOffset? LastReconciledAt { get; set; }

    /// <summary>What it found.</summary>
    public string? LastReconcileSummary { get; set; }

    /// <summary>Created.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Last changed.</summary>
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Who last changed it.</summary>
    public Guid? UpdatedByUserId { get; set; }
}

/// <summary>A person as the application's SCIM server knows them.</summary>
public class ScimUserLink
{
    /// <summary>The application.</summary>
    public Guid AppId { get; set; }

    /// <summary>The person.</summary>
    public Guid UserId { get; set; }

    /// <summary>The id the SCIM server gave them.</summary>
    public string RemoteId { get; set; } = string.Empty;

    /// <summary>Whether they are active there.</summary>
    public bool Active { get; set; }

    /// <summary>When they were last brought in step.</summary>
    public DateTimeOffset SyncedAt { get; set; }
}

/// <summary>A group Sangam created on the application's SCIM server.</summary>
public class ScimGroupLink
{
    /// <summary>The application.</summary>
    public Guid AppId { get; set; }

    /// <summary>The role code, or <c>role@org-id</c> with per-organisation mapping.</summary>
    public string GroupKey { get; set; } = string.Empty;

    /// <summary>The id the SCIM server gave it.</summary>
    public string RemoteId { get; set; } = string.Empty;

    /// <summary>Its display name there.</summary>
    public string DisplayName { get; set; } = string.Empty;
}

/// <summary>A person Sangam has put in a group on the application's SCIM server.</summary>
public class ScimGroupMember
{
    /// <summary>The application.</summary>
    public Guid AppId { get; set; }

    /// <summary>The group.</summary>
    public string GroupKey { get; set; } = string.Empty;

    /// <summary>The person.</summary>
    public Guid UserId { get; set; }
}

/// <summary>
/// One piece of SCIM work for an application — bringing one person in step, or renaming groups, or a reconciliation —
/// with its attempts, for the delivery log (SGM-216 §4).
/// </summary>
public class ScimDelivery
{
    /// <summary>Row number.</summary>
    public long Id { get; set; }

    /// <summary>The application.</summary>
    public Guid AppId { get; set; }

    /// <summary>The person, when the work is about one.</summary>
    public Guid? UserId { get; set; }

    /// <summary>Why: an event type, <c>reconcile</c>, <c>retry</c> or <c>groups</c>.</summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary><c>pending</c>, <c>done</c> or <c>dead</c> (gave up after its retries).</summary>
    public string Status { get; set; } = "pending";

    /// <summary>Attempts so far.</summary>
    public int Attempts { get; set; }

    /// <summary>When the next attempt is due.</summary>
    public DateTimeOffset NextAttemptAt { get; set; }

    /// <summary>Queued.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Finished (done or dead).</summary>
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>The calls made on the last attempt, for example <c>POST /Users 201 · PATCH /Groups/7 204</c>.</summary>
    public string? Summary { get; set; }

    /// <summary>The last HTTP status, if any.</summary>
    public int? LastStatusCode { get; set; }

    /// <summary>How long the last attempt took.</summary>
    public int? LatencyMs { get; set; }

    /// <summary>What went wrong on the last attempt (with the start of the server's answer).</summary>
    public string? LastError { get; set; }
}
