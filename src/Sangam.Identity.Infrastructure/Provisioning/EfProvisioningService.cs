using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Sangam.Identity.Application.Abstractions;
using Sangam.Identity.Application.Partners;
using Sangam.Identity.Application.Provisioning;
using Sangam.Identity.Domain;
using Sangam.Identity.Domain.Entities;
using Sangam.Identity.Domain.Enums;
using Sangam.Identity.Infrastructure.Persistence;

namespace Sangam.Identity.Infrastructure.Provisioning;

/// <summary>Whether Sangam may call private network addresses here (<see cref="OutboundHttp"/>).</summary>
/// <param name="AllowPrivate">Whether private addresses are allowed.</param>
public sealed record OutboundSettings(bool AllowPrivate);

/// <summary><see cref="IProvisioningService"/> over the identity database, for the application's administrators.</summary>
public sealed class EfProvisioningService : IProvisioningService
{
    private readonly SangamDbContext _db;
    private readonly ScimProvisioner _scim;
    private readonly OutboundSettings _outbound;
    private readonly IAuditWriter _audit;
    private readonly IClock _clock;

    /// <summary>Initialises the service.</summary>
    /// <param name="db">Database.</param>
    /// <param name="scim">The provisioner (token protection and the connection test).</param>
    /// <param name="outbound">Outbound address rules.</param>
    /// <param name="audit">Audit writer.</param>
    /// <param name="clock">Clock.</param>
    public EfProvisioningService(SangamDbContext db, ScimProvisioner scim, OutboundSettings outbound, IAuditWriter audit, IClock clock)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _scim = scim ?? throw new ArgumentNullException(nameof(scim));
        _outbound = outbound ?? throw new ArgumentNullException(nameof(outbound));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <inheritdoc />
    public async Task<ScimSettingsView?> GetScimAsync(Guid userId, Guid appId, CancellationToken cancellationToken = default)
    {
        if (!await AdministersAsync(userId, appId, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        ScimTarget? target = await _db.ScimTargets.AsNoTracking().FirstOrDefaultAsync(t => t.AppId == appId, cancellationToken).ConfigureAwait(false);
        var rows = await _db.ScimDeliveries.AsNoTracking().Where(d => d.AppId == appId).OrderByDescending(d => d.Id).Take(50)
            .Select(d => new { d.Id, d.CreatedAt, d.UserId, d.Reason, d.Status, d.Attempts, d.NextAttemptAt, d.Summary, d.LastStatusCode, d.LatencyMs, d.LastError })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        List<Guid> people = [.. rows.Where(r => r.UserId != null).Select(r => r.UserId!.Value).Distinct()];
        Dictionary<Guid, string> names = await _db.Users.AsNoTracking().Where(u => people.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => (u.FirstName + " " + u.LastName).Trim(), cancellationToken).ConfigureAwait(false);
        int provisioned = await _db.ScimUserLinks.CountAsync(l => l.AppId == appId && l.Active, cancellationToken).ConfigureAwait(false);
        List<ScimDeliveryRow> deliveries = [.. rows.Select(r => new ScimDeliveryRow(r.Id, r.CreatedAt, r.UserId is Guid u && names.TryGetValue(u, out string? n) ? n : null, r.Reason, r.Status, r.Attempts, r.Status == "pending" ? r.NextAttemptAt : null, r.Summary, r.LastStatusCode, r.LatencyMs, r.LastError))];
        return target is null
            ? new ScimSettingsView(false, string.Empty, "bearer", false, "role", false, false, "ok", null, null, null, null, 0, deliveries, true)
            : new ScimSettingsView(true, target.BaseUrl, target.AuthMode, target.ProtectedToken is not null, target.GroupMapping, target.DeleteOnDeprovision, target.Enabled, target.Status, target.LastSuccessAt, target.LastFailureAt, target.LastReconciledAt, target.LastReconcileSummary, provisioned, deliveries, true);
    }

    /// <inheritdoc />
    public async Task<PartnerResult> SaveScimAsync(Guid userId, Guid appId, ScimSettingsInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!await AdministersAsync(userId, appId, cancellationToken).ConfigureAwait(false))
        {
            return NotYours;
        }

        if (Validate(input) is string problem)
        {
            return PartnerResult.Refused(problem);
        }

        DateTimeOffset now = _clock.UtcNow;
        ScimTarget? target = await _db.ScimTargets.FirstOrDefaultAsync(t => t.AppId == appId, cancellationToken).ConfigureAwait(false);
        bool switchingOn = input.Enabled && target?.Enabled != true;
        if (target is null)
        {
            target = new ScimTarget { AppId = appId, CreatedAt = now };
            _db.ScimTargets.Add(target);
        }

        if (input.AuthMode == "bearer" && string.IsNullOrWhiteSpace(input.BearerToken) && target.ProtectedToken is null)
        {
            return PartnerResult.Refused("Enter the bearer token the SCIM server gave you.");
        }

        target.BaseUrl = input.BaseUrl.Trim().TrimEnd('/');
        target.AuthMode = input.AuthMode;
        if (input.AuthMode == "bearer" && !string.IsNullOrWhiteSpace(input.BearerToken))
        {
            target.ProtectedToken = _scim.Protect(input.BearerToken.Trim());
        }
        else if (input.AuthMode == "sangam")
        {
            target.ProtectedToken = null;
        }

        target.GroupMapping = input.GroupMapping;
        target.DeleteOnDeprovision = input.DeleteOnDeprovision;
        target.Enabled = input.Enabled;
        target.UpdatedAt = now;
        target.UpdatedByUserId = userId;
        if (switchingOn)
        {
            // Everyone who should be there is queued at once, rather than waiting for the night.
            target.Status = "ok";
            target.LastReconciledAt = null;
        }

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await _audit.WriteAsync(new AuditEntry(AuditActions.ScimSettings, AuditActorType.User, userId, appId, "app", appId,
            Metadata: JsonSerializer.Serialize(new { base_url = target.BaseUrl, auth = target.AuthMode, mapping = target.GroupMapping, enabled = target.Enabled, delete = target.DeleteOnDeprovision, token_changed = !string.IsNullOrWhiteSpace(input.BearerToken) })), cancellationToken).ConfigureAwait(false);
        return PartnerResult.Ok(switchingOn
            ? "Provisioning is on. Everyone with a role in the application is being sent now; the log below shows each delivery."
            : "Saved.");
    }

    /// <inheritdoc />
    public async Task<PartnerResult> TestScimAsync(Guid userId, Guid appId, ScimSettingsInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!await AdministersAsync(userId, appId, cancellationToken).ConfigureAwait(false))
        {
            return NotYours;
        }

        if (Validate(input) is string problem)
        {
            return PartnerResult.Refused(problem);
        }

        if (input.AuthMode == "sangam")
        {
            return PartnerResult.Refused("Sangam-signed tokens are made by the identity server, so they are tested by the first delivery: switch provisioning on and look at the log.");
        }

        string? token = input.BearerToken;
        if (string.IsNullOrWhiteSpace(token))
        {
            string? stored = await _db.ScimTargets.AsNoTracking().Where(t => t.AppId == appId).Select(t => t.ProtectedToken).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            if (stored is null)
            {
                return PartnerResult.Refused("Enter the bearer token the SCIM server gave you.");
            }

            token = _scim.Unprotect(stored);
        }

        ScimCall call = await _scim.TestAsync(input.BaseUrl.Trim().TrimEnd('/'), "bearer", token, cancellationToken).ConfigureAwait(false);
        string detail = call.Error ?? call.Line;
        return call.Ok
            ? PartnerResult.Ok($"The SCIM server answered: {call.Line}.")
            : PartnerResult.Refused($"The SCIM server did not accept the test ({detail}).");
    }

    /// <inheritdoc />
    public async Task<PartnerResult> ReconcileScimAsync(Guid userId, Guid appId, CancellationToken cancellationToken = default)
    {
        if (!await AdministersAsync(userId, appId, cancellationToken).ConfigureAwait(false))
        {
            return NotYours;
        }

        ScimTarget? target = await _db.ScimTargets.FirstOrDefaultAsync(t => t.AppId == appId && t.Enabled, cancellationToken).ConfigureAwait(false);
        if (target is null)
        {
            return PartnerResult.Refused("Switch provisioning on first.");
        }

        target.LastReconciledAt = null;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return PartnerResult.Ok("Reconciliation asked for: it runs within a minute, and its result appears here.");
    }

    /// <inheritdoc />
    public async Task<PartnerResult> RetryScimAsync(Guid userId, Guid appId, long deliveryId, CancellationToken cancellationToken = default)
    {
        if (!await AdministersAsync(userId, appId, cancellationToken).ConfigureAwait(false))
        {
            return NotYours;
        }

        ScimDelivery? dead = await _db.ScimDeliveries.AsNoTracking().FirstOrDefaultAsync(d => d.Id == deliveryId && d.AppId == appId && d.Status == "dead", cancellationToken).ConfigureAwait(false);
        if (dead is null)
        {
            return PartnerResult.Refused("Only a delivery that gave up can be tried again.");
        }

        if (await _db.ScimDeliveries.AnyAsync(d => d.AppId == appId && d.UserId == dead.UserId && d.Status == "pending", cancellationToken).ConfigureAwait(false))
        {
            return PartnerResult.Ok("A delivery for the same person is already waiting.");
        }

        DateTimeOffset now = _clock.UtcNow;
        _db.ScimDeliveries.Add(new ScimDelivery { AppId = appId, UserId = dead.UserId, Reason = "retry", CreatedAt = now, NextAttemptAt = now });
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return PartnerResult.Ok("Queued again.");
    }

    private string? Validate(ScimSettingsInput input)
    {
        if (OutboundHttp.Check(input.BaseUrl, _outbound.AllowPrivate) is string address)
        {
            return address;
        }

        if (input.AuthMode is not ("bearer" or "sangam"))
        {
            return "Choose how Sangam signs in to the SCIM server.";
        }

        return input.GroupMapping is "role" or "role_org" ? null : "Choose how roles become groups.";
    }

    private Task<bool> AdministersAsync(Guid userId, Guid appId, CancellationToken cancellationToken)
        => _db.AppAdmins.AsNoTracking().AnyAsync(a => a.UserId == userId && a.AppId == appId && a.RevokedAt == null && !a.App!.IsPlatform && a.App.Status == AppStatus.Active, cancellationToken);

    private static PartnerResult NotYours { get; } = PartnerResult.Refused("You do not administer this application.");
}
