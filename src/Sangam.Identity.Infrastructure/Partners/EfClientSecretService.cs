using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OpenIddict.Abstractions;
using OpenIddict.EntityFrameworkCore.Models;
using Sangam.Identity.Application.Abstractions;
using Sangam.Identity.Application.Partners;
using Sangam.Identity.Domain;
using Sangam.Identity.Domain.Entities;
using Sangam.Identity.Domain.Enums;
using Sangam.Identity.Infrastructure.Apps;
using Sangam.Identity.Infrastructure.Persistence;

namespace Sangam.Identity.Infrastructure.Partners;

/// <summary><see cref="IClientSecretService"/> over the identity database and OpenIddict's own hashing (rc.11, R5).</summary>
public sealed class EfClientSecretService : IClientSecretService
{
    private const string Prefix = "sgs_";

    private readonly SangamDbContext _db;
    private readonly SangamApplicationManager<OpenIddictEntityFrameworkCoreApplication> _applications;
    private readonly IAuditWriter _audit;
    private readonly IClock _clock;

    /// <summary>Initialises the service.</summary>
    /// <param name="db">Database.</param>
    /// <param name="applications">OpenIddict's application manager (Sangam's), for hashing.</param>
    /// <param name="audit">Audit writer.</param>
    /// <param name="clock">Clock.</param>
    public EfClientSecretService(SangamDbContext db, OpenIddict.Core.OpenIddictApplicationManager<OpenIddictEntityFrameworkCoreApplication> applications, IAuditWriter audit, IClock clock)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _applications = applications as SangamApplicationManager<OpenIddictEntityFrameworkCoreApplication>
            ?? throw new InvalidOperationException("Sangam's OpenIddict application manager is not registered.");
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <inheritdoc />
    public async Task<ClientSecretsView?> GetAsync(Guid userId, Guid appId, CancellationToken cancellationToken = default)
    {
        AppAdminRole? role = await RoleAsync(userId, appId, cancellationToken).ConfigureAwait(false);
        if (role is null)
        {
            return null;
        }

        var app = await _db.Apps.AsNoTracking().Where(a => a.Id == appId)
            .Select(a => new { a.ClientId, a.SecretSetAt, a.IssuedSecretRetiredAt })
            .FirstAsync(cancellationToken).ConfigureAwait(false);
        bool confidential = await IsConfidentialAsync(app.ClientId, cancellationToken).ConfigureAwait(false);
        if (!confidential)
        {
            return new ClientSecretsView(false, false, null, []);
        }

        DateTimeOffset now = _clock.UtcNow;
        var owned = await _db.ClientSecrets.AsNoTracking()
            .Where(s => s.AppId == appId && s.RevokedAt == null && s.ExpiresAt > now)
            .OrderByDescending(s => s.CreatedAt)
            .Select(s => new { s.Id, s.Hint, s.CreatedAt, s.CreatedByUserId, s.ExpiresAt, s.LastUsedAt })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        List<Guid> makers = [.. owned.Select(s => s.CreatedByUserId).Distinct()];
        Dictionary<Guid, string> names = await _db.Users.AsNoTracking().Where(u => makers.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => (u.FirstName + " " + u.LastName).Trim(), cancellationToken).ConfigureAwait(false);

        return new ClientSecretsView(
            true,
            role == AppAdminRole.Owner,
            app.IssuedSecretRetiredAt is null ? new IssuedSecretRow(app.SecretSetAt) : null,
            [.. owned.Select(s => new ClientSecretRow(s.Id, s.Hint, s.CreatedAt, names.GetValueOrDefault(s.CreatedByUserId) ?? string.Empty, s.ExpiresAt, s.LastUsedAt))]);
    }

    /// <inheritdoc />
    public async Task<NewClientSecret> CreateAsync(Guid userId, Guid appId, CancellationToken cancellationToken = default)
    {
        if (await RoleAsync(userId, appId, cancellationToken).ConfigureAwait(false) != AppAdminRole.Owner)
        {
            return new NewClientSecret(false, "Only an owner of this application can make or revoke its secrets.", null);
        }

        ClientSecretsView view = (await GetAsync(userId, appId, cancellationToken).ConfigureAwait(false))!;
        if (!view.Confidential)
        {
            return new NewClientSecret(false, "This application is a public client and uses no secret.", null);
        }

        if (view.LiveCount >= ClientSecret.MaxLive)
        {
            return new NewClientSecret(false, "Two secrets are live. Revoke the one your servers no longer use, then make a new one.", null);
        }

        string secret = Prefix + Base64Url(RandomNumberGenerator.GetBytes(32));
        DateTimeOffset now = _clock.UtcNow;
        ClientSecret row = new()
        {
            Id = Guid.NewGuid(),
            AppId = appId,
            Hash = await _applications.HashSecretAsync(secret, cancellationToken).ConfigureAwait(false),
            Hint = secret[..8] + "…" + secret[^3..],
            CreatedAt = now,
            CreatedByUserId = userId,
            ExpiresAt = now + ClientSecret.Lifetime,
        };
        _db.ClientSecrets.Add(row);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await _audit.WriteAsync(
            new AuditEntry(AuditActions.AppSecretCreate, AuditActorType.Admin, userId, appId, "app", appId,
                JsonSerializer.Serialize(new { hint = row.Hint, expires_at = row.ExpiresAt })),
            cancellationToken).ConfigureAwait(false);
        return new NewClientSecret(true, "Secret made. Copy it now: it is not shown again.", secret);
    }

    /// <inheritdoc />
    public async Task<PartnerResult> RevokeAsync(Guid userId, Guid appId, Guid? secretId, CancellationToken cancellationToken = default)
    {
        if (await RoleAsync(userId, appId, cancellationToken).ConfigureAwait(false) != AppAdminRole.Owner)
        {
            return PartnerResult.Refused("Only an owner of this application can make or revoke its secrets.");
        }

        ClientSecretsView view = (await GetAsync(userId, appId, cancellationToken).ConfigureAwait(false))!;
        if (view.LiveCount <= 1)
        {
            return PartnerResult.Refused("This is the only live secret. Make a new one and move your servers to it first.");
        }

        DateTimeOffset now = _clock.UtcNow;
        string which;
        if (secretId is Guid id)
        {
            ClientSecret? row = await _db.ClientSecrets.FirstOrDefaultAsync(s => s.Id == id && s.AppId == appId && s.RevokedAt == null, cancellationToken).ConfigureAwait(false);
            if (row is null)
            {
                return PartnerResult.Refused("That secret is not live.");
            }

            row.RevokedAt = now;
            row.RevokedByUserId = userId;
            which = row.Hint;
        }
        else
        {
            if (view.Issued is null)
            {
                return PartnerResult.Refused("That secret is not live.");
            }

            App app = await _db.Apps.FirstAsync(a => a.Id == appId, cancellationToken).ConfigureAwait(false);
            app.IssuedSecretRetiredAt = now;
            which = "issued";
        }

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await _audit.WriteAsync(
            new AuditEntry(AuditActions.AppSecretRevoke, AuditActorType.Admin, userId, appId, "app", appId, JsonSerializer.Serialize(new { secret = which })),
            cancellationToken).ConfigureAwait(false);
        return PartnerResult.Ok("Secret revoked. Anything still using it can no longer get tokens.");
    }

    private async Task<AppAdminRole?> RoleAsync(Guid userId, Guid appId, CancellationToken cancellationToken)
        => await _db.AppAdmins.AsNoTracking()
            .Where(a => a.AppId == appId && a.UserId == userId && a.RevokedAt == null && a.App!.Status == AppStatus.Active && !a.App.IsPlatform)
            .Select(a => (AppAdminRole?)a.Role)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

    private async Task<bool> IsConfidentialAsync(string clientId, CancellationToken cancellationToken)
        => string.Equals(
            await _db.Set<OpenIddictEntityFrameworkCoreApplication>().AsNoTracking().Where(c => c.ClientId == clientId).Select(c => c.ClientType).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false),
            OpenIddictConstants.ClientTypes.Confidential,
            StringComparison.Ordinal);

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
