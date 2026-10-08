using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Sangam.Identity.Application.Abstractions;
using Sangam.Identity.Application.Provisioning;
using Sangam.Identity.Domain;
using Sangam.Identity.Domain.Entities;
using Sangam.Identity.Domain.Enums;
using Sangam.Identity.Infrastructure.Persistence;

namespace Sangam.Identity.Infrastructure.Provisioning;

/// <summary><see cref="IWebhookService"/> over the identity database, for the application's administrators.</summary>
public sealed class EfWebhookService : IWebhookService
{
    /// <summary>How many endpoints an application may have.</summary>
    public const int MaxEndpoints = 10;

    /// <summary>How long the previous secret keeps signing after a rotation.</summary>
    public static readonly TimeSpan RotationOverlap = TimeSpan.FromHours(24);

    private readonly SangamDbContext _db;
    private readonly WebhookSender _sender;
    private readonly OutboundSettings _outbound;
    private readonly IAuditWriter _audit;
    private readonly IClock _clock;

    /// <summary>Initialises the service.</summary>
    /// <param name="db">Database.</param>
    /// <param name="sender">The sender (secret protection).</param>
    /// <param name="outbound">Outbound address rules.</param>
    /// <param name="audit">Audit writer.</param>
    /// <param name="clock">Clock.</param>
    public EfWebhookService(SangamDbContext db, WebhookSender sender, OutboundSettings outbound, IAuditWriter audit, IClock clock)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _sender = sender ?? throw new ArgumentNullException(nameof(sender));
        _outbound = outbound ?? throw new ArgumentNullException(nameof(outbound));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <inheritdoc />
    public async Task<WebhookOverview?> GetAsync(Guid userId, Guid appId, CancellationToken cancellationToken = default)
    {
        if (!await AdministersAsync(userId, appId, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        DateTimeOffset now = _clock.UtcNow;
        List<WebhookEndpoint> endpoints = await _db.WebhookEndpoints.AsNoTracking().Where(e => e.AppId == appId).OrderBy(e => e.CreatedAt).ToListAsync(cancellationToken).ConfigureAwait(false);
        Dictionary<Guid, string> urls = endpoints.ToDictionary(e => e.Id, e => e.Url);
        List<WebhookDelivery> deliveries = await _db.WebhookDeliveries.AsNoTracking().Where(d => d.AppId == appId).OrderByDescending(d => d.Id).Take(50).ToListAsync(cancellationToken).ConfigureAwait(false);
        return new WebhookOverview(
            [.. endpoints.Select(e => new WebhookEndpointView(e.Id, e.Url, e.Description, Events(e.Events), e.Enabled, e.Status, e.LastSuccessAt, e.LastFailureAt, e.PreviousSecretExpiresAt > now ? e.PreviousSecretExpiresAt : null))],
            [.. deliveries.Select(d => new WebhookDeliveryRow(d.Id, d.EndpointId, urls.GetValueOrDefault(d.EndpointId) ?? string.Empty, "evt_" + d.EventId.ToString("N"), d.EventType, d.CreatedAt, d.Status, d.Attempts, d.Status == "pending" ? d.NextAttemptAt : null, d.LastStatusCode, d.LatencyMs, d.ResponseSnippet, d.LastError, d.Payload))]);
    }

    /// <inheritdoc />
    public async Task<WebhookResult> AddAsync(Guid userId, Guid appId, WebhookEndpointInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!await AdministersAsync(userId, appId, cancellationToken).ConfigureAwait(false))
        {
            return NotYours;
        }

        if (Validate(input) is string problem)
        {
            return new WebhookResult(false, problem);
        }

        if (await _db.WebhookEndpoints.CountAsync(e => e.AppId == appId, cancellationToken).ConfigureAwait(false) >= MaxEndpoints)
        {
            return new WebhookResult(false, $"An application can have at most {MaxEndpoints} endpoints.");
        }

        DateTimeOffset now = _clock.UtcNow;
        string secret = WebhookSigner.NewSecret();
        WebhookEndpoint endpoint = new()
        {
            Id = Guid.NewGuid(),
            AppId = appId,
            Url = input.Url.Trim(),
            Description = Clean(input.Description),
            Events = string.Join(',', input.Events.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)),
            ProtectedSecret = _sender.Protect(secret),
            Enabled = input.Enabled,
            CreatedAt = now,
            UpdatedAt = now,
            UpdatedByUserId = userId,
        };
        _db.WebhookEndpoints.Add(endpoint);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await AuditAsync(AuditActions.WebhookEndpointSave, userId, appId, endpoint, cancellationToken).ConfigureAwait(false);
        return new WebhookResult(true, "Endpoint added. Copy its signing secret now: it is not shown again.", secret);
    }

    /// <inheritdoc />
    public async Task<WebhookResult> UpdateAsync(Guid userId, Guid appId, Guid endpointId, WebhookEndpointInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        WebhookEndpoint? endpoint = await FindAsync(userId, appId, endpointId, cancellationToken).ConfigureAwait(false);
        if (endpoint is null)
        {
            return NotYours;
        }

        if (Validate(input) is string problem)
        {
            return new WebhookResult(false, problem);
        }

        endpoint.Url = input.Url.Trim();
        endpoint.Description = Clean(input.Description);
        endpoint.Events = string.Join(',', input.Events.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
        if (input.Enabled && !endpoint.Enabled)
        {
            endpoint.Status = "ok";
        }

        endpoint.Enabled = input.Enabled;
        endpoint.UpdatedAt = _clock.UtcNow;
        endpoint.UpdatedByUserId = userId;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await AuditAsync(AuditActions.WebhookEndpointSave, userId, appId, endpoint, cancellationToken).ConfigureAwait(false);
        return new WebhookResult(true, "Saved.");
    }

    /// <inheritdoc />
    public async Task<WebhookResult> RemoveAsync(Guid userId, Guid appId, Guid endpointId, CancellationToken cancellationToken = default)
    {
        WebhookEndpoint? endpoint = await FindAsync(userId, appId, endpointId, cancellationToken).ConfigureAwait(false);
        if (endpoint is null)
        {
            return NotYours;
        }

        _db.WebhookEndpoints.Remove(endpoint);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await AuditAsync(AuditActions.WebhookEndpointDelete, userId, appId, endpoint, cancellationToken).ConfigureAwait(false);
        return new WebhookResult(true, "Endpoint removed, with its delivery log.");
    }

    /// <inheritdoc />
    public async Task<WebhookResult> RotateSecretAsync(Guid userId, Guid appId, Guid endpointId, CancellationToken cancellationToken = default)
    {
        WebhookEndpoint? endpoint = await FindAsync(userId, appId, endpointId, cancellationToken).ConfigureAwait(false);
        if (endpoint is null)
        {
            return NotYours;
        }

        DateTimeOffset now = _clock.UtcNow;
        string secret = WebhookSigner.NewSecret();
        endpoint.ProtectedPreviousSecret = endpoint.ProtectedSecret;
        endpoint.PreviousSecretExpiresAt = now + RotationOverlap;
        endpoint.ProtectedSecret = _sender.Protect(secret);
        endpoint.UpdatedAt = now;
        endpoint.UpdatedByUserId = userId;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await AuditAsync(AuditActions.WebhookSecretRotate, userId, appId, endpoint, cancellationToken).ConfigureAwait(false);
        return new WebhookResult(true, "New signing secret made. Copy it now: it is not shown again. For 24 hours every message is signed with both, so your receiver can switch over.", secret);
    }

    /// <inheritdoc />
    public async Task<WebhookResult> SendTestAsync(Guid userId, Guid appId, Guid endpointId, CancellationToken cancellationToken = default)
    {
        WebhookEndpoint? endpoint = await FindAsync(userId, appId, endpointId, cancellationToken).ConfigureAwait(false);
        if (endpoint is null)
        {
            return NotYours;
        }

        DateTimeOffset now = _clock.UtcNow;
        string clientId = await _db.Apps.AsNoTracking().Where(a => a.Id == appId).Select(a => a.ClientId).FirstAsync(cancellationToken).ConfigureAwait(false);
        AppEvent ping = new() { Id = Guid.CreateVersion7(now), AppId = appId, Type = AppEventTypes.Ping, Data = JsonSerializer.Serialize(new { message = "A test from the Sangam partner console." }) /* i18n-ignore: sent to the receiver as data */, CreatedAt = now };
        _db.WebhookDeliveries.Add(new WebhookDelivery { EndpointId = endpoint.Id, AppId = appId, EventId = ping.Id, EventType = ping.Type, Payload = AppEventDispatcher.Payload(ping, clientId, null), CreatedAt = now, NextAttemptAt = now });
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new WebhookResult(true, "A test event is on its way; it appears in the log below within seconds.");
    }

    /// <inheritdoc />
    public async Task<WebhookResult> ReplayAsync(Guid userId, Guid appId, long deliveryId, CancellationToken cancellationToken = default)
    {
        if (!await AdministersAsync(userId, appId, cancellationToken).ConfigureAwait(false))
        {
            return NotYours;
        }

        WebhookDelivery? original = await _db.WebhookDeliveries.AsNoTracking().FirstOrDefaultAsync(d => d.Id == deliveryId && d.AppId == appId, cancellationToken).ConfigureAwait(false);
        if (original is null || original.Status == "pending")
        {
            return new WebhookResult(false, "Only a finished delivery can be sent again.");
        }

        DateTimeOffset now = _clock.UtcNow;
        _db.WebhookDeliveries.Add(new WebhookDelivery { EndpointId = original.EndpointId, AppId = appId, EventId = original.EventId, EventType = original.EventType, Payload = original.Payload, CreatedAt = now, NextAttemptAt = now });
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new WebhookResult(true, "Sent again, with the same webhook-id.");
    }

    private string? Validate(WebhookEndpointInput input)
    {
        if (OutboundHttp.Check(input.Url, _outbound.AllowPrivate) is string address)
        {
            return address;
        }

        if (input.Events.Count == 0)
        {
            return "Choose at least one event.";
        }

        return input.Events.All(AppEventTypes.All.Contains) ? null : "One of the events is not one Sangam sends.";
    }

    private async Task<WebhookEndpoint?> FindAsync(Guid userId, Guid appId, Guid endpointId, CancellationToken cancellationToken)
        => await AdministersAsync(userId, appId, cancellationToken).ConfigureAwait(false)
            ? await _db.WebhookEndpoints.FirstOrDefaultAsync(e => e.Id == endpointId && e.AppId == appId, cancellationToken).ConfigureAwait(false)
            : null;

    private Task<bool> AdministersAsync(Guid userId, Guid appId, CancellationToken cancellationToken)
        => _db.AppAdmins.AsNoTracking().AnyAsync(a => a.UserId == userId && a.AppId == appId && a.RevokedAt == null && !a.App!.IsPlatform && a.App.Status == AppStatus.Active, cancellationToken);

    private Task AuditAsync(string action, Guid userId, Guid appId, WebhookEndpoint endpoint, CancellationToken cancellationToken)
        => _audit.WriteAsync(new AuditEntry(action, AuditActorType.User, userId, appId, "webhook_endpoint", endpoint.Id,
            Metadata: JsonSerializer.Serialize(new { url = endpoint.Url, events = endpoint.Events, enabled = endpoint.Enabled })), cancellationToken);

    private static IReadOnlyList<string> Events(string value) => [.. value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim().Length > 200 ? value.Trim()[..200] : value.Trim();

    private static WebhookResult NotYours { get; } = new(false, "You do not administer this application.");
}
