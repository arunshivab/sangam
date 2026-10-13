using Microsoft.EntityFrameworkCore;
using OpenIddict.EntityFrameworkCore.Models;
using Sangam.Identity.Application.Partners;

namespace Sangam.Identity.Infrastructure.Partners;

/// <summary>rc.10 (R1, R2): the partner console's application overview.</summary>
public sealed partial class EfPartnerService
{
    /// <inheritdoc />
    public async Task<PartnerAppOverview?> GetOverviewAsync(Guid userId, Guid appId, CancellationToken cancellationToken = default)
    {
        if (await CheckRoleAsync(userId, appId, cancellationToken).ConfigureAwait(false) is null)
        {
            return null;
        }

        Domain.Entities.App? app = await _db.Apps.AsNoTracking().SingleOrDefaultAsync(a => a.Id == appId, cancellationToken).ConfigureAwait(false);
        if (app is null)
        {
            return null;
        }

        DateTimeOffset since = _clock.UtcNow.AddDays(-1);
        int people = await _db.AppGrants.CountAsync(g => g.AppId == appId && g.RevokedAt == null, cancellationToken).ConfigureAwait(false);
        int organisations = await _db.Organisations.CountAsync(o => o.RegisteredViaAppId == appId && o.DeletedAt == null, cancellationToken).ConfigureAwait(false);
        int roles = await _db.Roles.CountAsync(r => r.AppId == appId && r.RetiredAt == null, cancellationToken).ConfigureAwait(false);
        int administrators = await _db.AppAdmins.CountAsync(a => a.AppId == appId && a.RevokedAt == null, cancellationToken).ConfigureAwait(false);
        int signIns = await _db.SessionApps.Where(s => s.AppId == appId && s.FirstSeenAt > since).Select(s => s.UserId).Distinct().CountAsync(cancellationToken).ConfigureAwait(false);
        var scim = await _db.ScimTargets.AsNoTracking().Where(t => t.AppId == appId)
            .Select(t => new { t.Enabled, t.Status, t.LastSuccessAt })
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        List<string> hooks = await _db.WebhookEndpoints.AsNoTracking().Where(w => w.AppId == appId && w.Enabled)
            .Select(w => w.Status)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        int waiting = await _db.WebhookDeliveries.CountAsync(d => d.AppId == appId && d.Status == "pending", cancellationToken).ConfigureAwait(false);
        string? clientType = await _db.Set<OpenIddictEntityFrameworkCoreApplication>().AsNoTracking()
            .Where(o => o.ClientId == app.ClientId)
            .Select(o => o.ClientType)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        // rc.11: once the owner retired the secret Sangam issued, the age is that of the oldest secret still live.
        DateTimeOffset now = _clock.UtcNow;
        DateTimeOffset? secretSetAt = app.IssuedSecretRetiredAt is null
            ? app.SecretSetAt
            : await _db.ClientSecrets.AsNoTracking().Where(x => x.AppId == appId && x.RevokedAt == null && x.ExpiresAt > now).MinAsync(x => (DateTimeOffset?)x.CreatedAt, cancellationToken).ConfigureAwait(false);

        return new PartnerAppOverview(
            people,
            organisations,
            roles,
            administrators,
            signIns,
            scim?.Enabled == true,
            scim is { Enabled: true } && scim.Status != "ok",
            scim?.LastSuccessAt,
            hooks.Count,
            hooks.Count(h => h != "ok"),
            waiting,
            !string.IsNullOrEmpty(app.BackChannelLogoutUri) || !string.IsNullOrEmpty(app.FrontChannelLogoutUri),
            secretSetAt,
            clientType is null ? null : string.Equals(clientType, OpenIddict.Abstractions.OpenIddictConstants.ClientTypes.Confidential, StringComparison.Ordinal),
            app.Status);
    }
}
