using Sangam.Identity.Application.Partners;

namespace Sangam.Identity.Application.Provisioning;

/// <summary>
/// An application's SCIM provisioning, as its administrators see it on the partner console (PR-23, SGM-216 §5): where
/// Sangam sends people, how it authenticates, how roles become groups, and what happened to every delivery.
/// </summary>
public interface IProvisioningService
{
    /// <summary>The settings and the recent deliveries, or <see langword="null"/> for someone who does not administer the application.</summary>
    /// <param name="userId">The administrator.</param>
    /// <param name="appId">The application.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<ScimSettingsView?> GetScimAsync(Guid userId, Guid appId, CancellationToken cancellationToken = default);

    /// <summary>Saves the settings; switching on queues everyone who should be provisioned.</summary>
    /// <param name="userId">The administrator.</param>
    /// <param name="appId">The application.</param>
    /// <param name="input">The settings.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<PartnerResult> SaveScimAsync(Guid userId, Guid appId, ScimSettingsInput input, CancellationToken cancellationToken = default);

    /// <summary>Asks the SCIM server for its configuration with these settings (a bearer token typed now, or the stored one).</summary>
    /// <param name="userId">The administrator.</param>
    /// <param name="appId">The application.</param>
    /// <param name="input">The settings to test.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<PartnerResult> TestScimAsync(Guid userId, Guid appId, ScimSettingsInput input, CancellationToken cancellationToken = default);

    /// <summary>Asks for a reconciliation now (the identity server runs it within a minute).</summary>
    /// <param name="userId">The administrator.</param>
    /// <param name="appId">The application.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<PartnerResult> ReconcileScimAsync(Guid userId, Guid appId, CancellationToken cancellationToken = default);

    /// <summary>Tries a delivery that gave up again.</summary>
    /// <param name="userId">The administrator.</param>
    /// <param name="appId">The application.</param>
    /// <param name="deliveryId">The delivery.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<PartnerResult> RetryScimAsync(Guid userId, Guid appId, long deliveryId, CancellationToken cancellationToken = default);
}

/// <summary>SCIM settings as entered.</summary>
/// <param name="BaseUrl">The SCIM base address.</param>
/// <param name="AuthMode"><c>bearer</c> or <c>sangam</c>.</param>
/// <param name="BearerToken">A new bearer token, or null to keep the stored one.</param>
/// <param name="GroupMapping"><c>role</c> or <c>role_org</c>.</param>
/// <param name="DeleteOnDeprovision">Delete people who lose access instead of deactivating them.</param>
/// <param name="Enabled">Whether provisioning is on.</param>
public sealed record ScimSettingsInput(string BaseUrl, string AuthMode, string? BearerToken, string GroupMapping, bool DeleteOnDeprovision, bool Enabled);

/// <summary>SCIM settings and state as shown.</summary>
/// <param name="Configured">Whether settings exist.</param>
/// <param name="BaseUrl">The SCIM base address.</param>
/// <param name="AuthMode"><c>bearer</c> or <c>sangam</c>.</param>
/// <param name="HasToken">Whether a bearer token is stored (it is never shown).</param>
/// <param name="GroupMapping"><c>role</c> or <c>role_org</c>.</param>
/// <param name="DeleteOnDeprovision">Delete instead of deactivate.</param>
/// <param name="Enabled">Whether provisioning is on.</param>
/// <param name="Status"><c>ok</c> or <c>failing</c>.</param>
/// <param name="LastSuccessAt">Last success.</param>
/// <param name="LastFailureAt">Last failure.</param>
/// <param name="LastReconciledAt">Last reconciliation.</param>
/// <param name="LastReconcileSummary">What it found.</param>
/// <param name="Provisioned">People currently active on the server.</param>
/// <param name="Deliveries">The most recent deliveries, newest first.</param>
/// <param name="SangamSignedTokens">Whether Sangam-signed tokens are described (the issuer and JWKS address).</param>
public sealed record ScimSettingsView(
    bool Configured,
    string BaseUrl,
    string AuthMode,
    bool HasToken,
    string GroupMapping,
    bool DeleteOnDeprovision,
    bool Enabled,
    string Status,
    DateTimeOffset? LastSuccessAt,
    DateTimeOffset? LastFailureAt,
    DateTimeOffset? LastReconciledAt,
    string? LastReconcileSummary,
    int Provisioned,
    IReadOnlyList<ScimDeliveryRow> Deliveries,
    bool SangamSignedTokens);

/// <summary>One delivery in the log.</summary>
/// <param name="Id">Delivery id.</param>
/// <param name="CreatedAt">Queued.</param>
/// <param name="Person">Who it was about (name), or null.</param>
/// <param name="Reason">Why (an event type, reconcile, retry, groups).</param>
/// <param name="Status">pending, done or dead.</param>
/// <param name="Attempts">Attempts so far.</param>
/// <param name="NextAttemptAt">When the next attempt is due (pending only).</param>
/// <param name="Summary">The calls made.</param>
/// <param name="LastStatusCode">The last HTTP status.</param>
/// <param name="LatencyMs">How long the last attempt took.</param>
/// <param name="LastError">What went wrong.</param>
public sealed record ScimDeliveryRow(long Id, DateTimeOffset CreatedAt, string? Person, string Reason, string Status, int Attempts, DateTimeOffset? NextAttemptAt, string? Summary, int? LastStatusCode, int? LatencyMs, string? LastError);
