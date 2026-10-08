using Sangam.Identity.Application.Partners;

namespace Sangam.Identity.Application.Provisioning;

/// <summary>
/// An application's webhook endpoints and their delivery log, as its administrators see them on the partner console
/// (PR-24, SGM-217). A secret is shown once, when an endpoint is added or its secret rotated, and never again.
/// </summary>
public interface IWebhookService
{
    /// <summary>The endpoints and recent deliveries, or <see langword="null"/> for someone who does not administer the application.</summary>
    /// <param name="userId">The administrator.</param>
    /// <param name="appId">The application.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<WebhookOverview?> GetAsync(Guid userId, Guid appId, CancellationToken cancellationToken = default);

    /// <summary>Adds an endpoint; the result carries its secret, shown this once.</summary>
    /// <param name="userId">The administrator.</param>
    /// <param name="appId">The application.</param>
    /// <param name="input">The endpoint.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<WebhookResult> AddAsync(Guid userId, Guid appId, WebhookEndpointInput input, CancellationToken cancellationToken = default);

    /// <summary>Changes an endpoint's address, events, description or switch.</summary>
    /// <param name="userId">The administrator.</param>
    /// <param name="appId">The application.</param>
    /// <param name="endpointId">The endpoint.</param>
    /// <param name="input">The changes.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<WebhookResult> UpdateAsync(Guid userId, Guid appId, Guid endpointId, WebhookEndpointInput input, CancellationToken cancellationToken = default);

    /// <summary>Removes an endpoint and its log.</summary>
    /// <param name="userId">The administrator.</param>
    /// <param name="appId">The application.</param>
    /// <param name="endpointId">The endpoint.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<WebhookResult> RemoveAsync(Guid userId, Guid appId, Guid endpointId, CancellationToken cancellationToken = default);

    /// <summary>A new secret, shown this once; the old one keeps signing for 24 hours, so receivers can switch over.</summary>
    /// <param name="userId">The administrator.</param>
    /// <param name="appId">The application.</param>
    /// <param name="endpointId">The endpoint.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<WebhookResult> RotateSecretAsync(Guid userId, Guid appId, Guid endpointId, CancellationToken cancellationToken = default);

    /// <summary>Queues a <c>ping</c> event to one endpoint.</summary>
    /// <param name="userId">The administrator.</param>
    /// <param name="appId">The application.</param>
    /// <param name="endpointId">The endpoint.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<WebhookResult> SendTestAsync(Guid userId, Guid appId, Guid endpointId, CancellationToken cancellationToken = default);

    /// <summary>Sends a delivery again, with the same body and <c>webhook-id</c> (receivers drop duplicates).</summary>
    /// <param name="userId">The administrator.</param>
    /// <param name="appId">The application.</param>
    /// <param name="deliveryId">The delivery.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<WebhookResult> ReplayAsync(Guid userId, Guid appId, long deliveryId, CancellationToken cancellationToken = default);
}

/// <summary>An endpoint as entered.</summary>
/// <param name="Url">The https address.</param>
/// <param name="Events">The event types to receive.</param>
/// <param name="Description">What it is for.</param>
/// <param name="Enabled">Whether deliveries are made.</param>
public sealed record WebhookEndpointInput(string Url, IReadOnlyList<string> Events, string? Description, bool Enabled);

/// <summary>The outcome of a change; <see cref="Secret"/> is set only when a secret was made.</summary>
/// <param name="Succeeded">Whether it was done.</param>
/// <param name="Message">What to tell the person.</param>
/// <param name="Secret">The new signing secret, to show once.</param>
public sealed record WebhookResult(bool Succeeded, string? Message, string? Secret = null)
{
    /// <summary>As a partner result.</summary>
    public PartnerResult ToPartnerResult() => new(Succeeded, Message);
}

/// <summary>The endpoints and the delivery log.</summary>
/// <param name="Endpoints">The endpoints.</param>
/// <param name="Deliveries">The most recent deliveries, newest first.</param>
public sealed record WebhookOverview(IReadOnlyList<WebhookEndpointView> Endpoints, IReadOnlyList<WebhookDeliveryRow> Deliveries);

/// <summary>An endpoint as shown.</summary>
/// <param name="Id">Endpoint id.</param>
/// <param name="Url">Address.</param>
/// <param name="Description">What it is for.</param>
/// <param name="Events">Event types.</param>
/// <param name="Enabled">Whether deliveries are made.</param>
/// <param name="Status">ok or failing.</param>
/// <param name="LastSuccessAt">Last success.</param>
/// <param name="LastFailureAt">Last failure.</param>
/// <param name="PreviousSecretUntil">While the previous secret still signs, until when.</param>
public sealed record WebhookEndpointView(Guid Id, string Url, string? Description, IReadOnlyList<string> Events, bool Enabled, string Status, DateTimeOffset? LastSuccessAt, DateTimeOffset? LastFailureAt, DateTimeOffset? PreviousSecretUntil);

/// <summary>One delivery in the log.</summary>
/// <param name="Id">Delivery id.</param>
/// <param name="EndpointId">Endpoint.</param>
/// <param name="EndpointUrl">Its address.</param>
/// <param name="MessageId">The <c>webhook-id</c>.</param>
/// <param name="EventType">Event type.</param>
/// <param name="CreatedAt">Queued.</param>
/// <param name="Status">pending, done or dead.</param>
/// <param name="Attempts">Attempts so far.</param>
/// <param name="NextAttemptAt">When the next attempt is due.</param>
/// <param name="LastStatusCode">The last HTTP status.</param>
/// <param name="LatencyMs">How long the last attempt took.</param>
/// <param name="ResponseSnippet">The start of the answer.</param>
/// <param name="LastError">What went wrong.</param>
/// <param name="Payload">The body sent.</param>
public sealed record WebhookDeliveryRow(long Id, Guid EndpointId, string EndpointUrl, string MessageId, string EventType, DateTimeOffset CreatedAt, string Status, int Attempts, DateTimeOffset? NextAttemptAt, int? LastStatusCode, int? LatencyMs, string? ResponseSnippet, string? LastError, string Payload);
