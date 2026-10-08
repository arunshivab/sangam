using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sangam.Identity.Application.Abstractions;
using Sangam.Identity.Domain;
using Sangam.Identity.Domain.Entities;
using Sangam.Identity.Domain.Enums;
using Sangam.Identity.Infrastructure.Persistence;

namespace Sangam.Identity.Infrastructure.Provisioning;

/// <summary>
/// Delivers webhooks (PR-24, SGM-217 §4): a POST of the stored body with the Standard Webhooks headers, signed with the
/// endpoint's secret (and, for a day after a rotation, its previous one). A 2xx answer is success; anything else, or
/// no answer within fifteen seconds, is tried again after 1 min, 5 min, 30 min, 2 h, 6 h and 12 h. Then the delivery
/// gives up, the endpoint is marked failing and the application's owners are told. Every attempt is in the log with its
/// status, latency and the start of the answer. Runs on the identity server only.
/// </summary>
public sealed partial class WebhookSender
{
    /// <summary>The data-protection purpose for webhook secrets.</summary>
    public const string SecretPurpose = "Sangam.Webhooks.Secret.v1";

    /// <summary>The user agent Sangam sends.</summary>
    public const string UserAgent = "Sangam-Webhooks/1";

    private readonly SangamDbContext _db;
    private readonly IHttpClientFactory _http;
    private readonly IDataProtector _protector;
    private readonly IntegrationAlerts _alerts;
    private readonly IAuditWriter _audit;
    private readonly IClock _clock;
    private readonly ILogger<WebhookSender> _logger;

    /// <summary>Initialises the sender.</summary>
    /// <param name="db">Database.</param>
    /// <param name="http">HTTP clients (<see cref="OutboundHttp.ClientName"/>).</param>
    /// <param name="protection">Data protection, for the secrets.</param>
    /// <param name="alerts">Tells the owners when an endpoint fails.</param>
    /// <param name="audit">Audit writer.</param>
    /// <param name="clock">Clock.</param>
    /// <param name="logger">Logger.</param>
    public WebhookSender(SangamDbContext db, IHttpClientFactory http, IDataProtectionProvider protection, IntegrationAlerts alerts, IAuditWriter audit, IClock clock, ILogger<WebhookSender> logger)
    {
        ArgumentNullException.ThrowIfNull(protection);
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _protector = protection.CreateProtector(SecretPurpose);
        _alerts = alerts ?? throw new ArgumentNullException(nameof(alerts));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Protects a secret for storage.</summary>
    /// <param name="secret">The secret.</param>
    public string Protect(string secret) => _protector.Protect(secret);

    /// <summary>Delivers everything that is due; returns how many succeeded.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<int> DeliverDueAsync(CancellationToken cancellationToken = default)
    {
        DateTimeOffset now = _clock.UtcNow;
        List<WebhookDelivery> due = await _db.WebhookDeliveries.Where(d => d.Status == "pending" && d.NextAttemptAt <= now).OrderBy(d => d.Id).Take(50).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (due.Count == 0)
        {
            return 0;
        }

        List<Guid> ids = [.. due.Select(d => d.EndpointId).Distinct()];
        Dictionary<Guid, WebhookEndpoint> endpoints = await _db.WebhookEndpoints.Where(e => ids.Contains(e.Id)).ToDictionaryAsync(e => e.Id, cancellationToken).ConfigureAwait(false);
        HttpClient client = _http.CreateClient(OutboundHttp.ClientName);
        int succeeded = 0;
        foreach (WebhookDelivery delivery in due)
        {
            if (!endpoints.TryGetValue(delivery.EndpointId, out WebhookEndpoint? endpoint) || !endpoint.Enabled)
            {
                delivery.Status = "dead";
                delivery.CompletedAt = _clock.UtcNow;
                delivery.LastError = "The endpoint is switched off."; // i18n-ignore: kept in the delivery log as written
                continue;
            }

            if (await SendAsync(client, endpoint, delivery, cancellationToken).ConfigureAwait(false))
            {
                succeeded++;
            }
        }

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return succeeded;
    }

    private async Task<bool> SendAsync(HttpClient client, WebhookEndpoint endpoint, WebhookDelivery delivery, CancellationToken cancellationToken)
    {
        delivery.Attempts++;
        DateTimeOffset now = _clock.UtcNow;
        string id = "evt_" + delivery.EventId.ToString("N");
        long timestamp = now.ToUnixTimeSeconds();
        List<string> secrets = [_protector.Unprotect(endpoint.ProtectedSecret)];
        if (endpoint.ProtectedPreviousSecret is { Length: > 0 } previous && endpoint.PreviousSecretExpiresAt > now)
        {
            secrets.Add(_protector.Unprotect(previous));
        }

        Stopwatch watch = Stopwatch.StartNew();
        int? status = null;
        string? snippet = null;
        string? error = null;
        try
        {
            using HttpRequestMessage request = new(HttpMethod.Post, new Uri(endpoint.Url, UriKind.Absolute));
            request.Headers.TryAddWithoutValidation("webhook-id", id);
            request.Headers.TryAddWithoutValidation("webhook-timestamp", timestamp.ToString(CultureInfo.InvariantCulture));
            request.Headers.TryAddWithoutValidation("webhook-signature", WebhookSigner.Sign(id, timestamp, delivery.Payload, [.. secrets]));
            request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            request.Content = new StringContent(delivery.Payload, Encoding.UTF8, "application/json");
            using HttpResponseMessage response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            status = (int)response.StatusCode;
            string text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            snippet = text.Length > 500 ? text[..500] : text;
            if (!response.IsSuccessStatusCode)
            {
                error = "HTTP " + status.Value.ToString(CultureInfo.InvariantCulture);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or UriFormatException)
        {
            error = ex is TaskCanceledException ? "No answer within 15 seconds." : ex.Message; // i18n-ignore: kept in the delivery log as written
        }

        delivery.LatencyMs = (int)watch.ElapsedMilliseconds;
        delivery.LastStatusCode = status;
        delivery.ResponseSnippet = snippet;
        now = _clock.UtcNow;
        if (error is null)
        {
            delivery.Status = "done";
            delivery.CompletedAt = now;
            delivery.LastError = null;
            endpoint.LastSuccessAt = now;
            endpoint.Status = "ok";
            return true;
        }

        delivery.LastError = error.Length > 1000 ? error[..1000] : error;
        endpoint.LastFailureAt = now;
        if (delivery.Attempts > ScimProvisioner.Backoff.Count)
        {
            delivery.Status = "dead";
            delivery.CompletedAt = now;
            if (endpoint.Status != "failing")
            {
                endpoint.Status = "failing";
                await _audit.WriteAsync(new AuditEntry(AuditActions.WebhookFailing, AuditActorType.System, TargetType: "webhook_endpoint", TargetId: endpoint.Id, Metadata: System.Text.Json.JsonSerializer.Serialize(new { app = endpoint.AppId, delivery = delivery.Id, error = delivery.LastError })), cancellationToken).ConfigureAwait(false);
                await _alerts.FailingAsync(endpoint.AppId, "the webhook endpoint " + endpoint.Url, "webhooks", delivery.LastError, cancellationToken).ConfigureAwait(false);
            }

            LogGaveUp(endpoint.Id, delivery.Id, delivery.LastError);
        }
        else
        {
            delivery.NextAttemptAt = now + ScimProvisioner.Backoff[delivery.Attempts - 1];
        }

        return false;
    }

    [LoggerMessage(EventId = 2201, Level = LogLevel.Warning, Message = "Webhook delivery {Delivery} to endpoint {Endpoint} given up: {Error}")]
    private partial void LogGaveUp(Guid endpoint, long delivery, string? error);
}
