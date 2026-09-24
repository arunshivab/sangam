using Microsoft.EntityFrameworkCore;
using Sangam.Identity.Application.Abstractions;
using Sangam.Identity.Application.Consents;
using Sangam.Identity.Domain;
using Sangam.Identity.Domain.Entities;
using Sangam.Identity.Domain.Enums;
using Sangam.Identity.Infrastructure.Persistence;

namespace Sangam.Identity.Infrastructure.Consents;

/// <summary><see cref="IConsentService"/> over the <c>consents</c> and <c>app_grants</c> tables.</summary>
public sealed class EfConsentService : IConsentService
{
    private readonly SangamDbContext _db;
    private readonly IClock _clock;
    private readonly IAuditWriter _audit;

    /// <summary>Initialises the service.</summary>
    /// <param name="db">Database.</param>
    /// <param name="clock">Clock.</param>
    /// <param name="audit">Audit writer.</param>
    public EfConsentService(SangamDbContext db, IClock clock, IAuditWriter audit)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
    }

    /// <inheritdoc />
    public async Task<bool> HasValidConsentAsync(Guid userId, Guid appId, IReadOnlyCollection<string> requestedScopes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requestedScopes);

        string version = await _db.Apps.Where(a => a.Id == appId).Select(a => a.ConsentVersion).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false)
            ?? string.Empty;

        Domain.Entities.Consent? live = await _db.Consents
            .AsNoTracking()
            .Where(c => c.UserId == userId && c.AppId == appId && c.RevokedAt == null)
            .OrderByDescending(c => c.GrantedAt)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (live is null || !string.Equals(live.ConsentVersion, version, StringComparison.Ordinal))
        {
            return false;
        }

        HashSet<string> granted = new(live.Scope.Split(' ', StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal);
        return requestedScopes.All(granted.Contains);
    }

    /// <inheritdoc />
    public async Task GrantAsync(Guid userId, Guid appId, IReadOnlyCollection<string> scopes, string? ipAddress, string? userAgent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        DateTimeOffset now = _clock.UtcNow;

        string version = await _db.Apps.Where(a => a.Id == appId).Select(a => a.ConsentVersion).FirstAsync(cancellationToken).ConfigureAwait(false);

        await _db.Consents
            .Where(c => c.UserId == userId && c.AppId == appId && c.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.RevokedAt, now), cancellationToken)
            .ConfigureAwait(false);

        string scope = string.Join(' ', scopes.Distinct(StringComparer.Ordinal).OrderBy(s => s, StringComparer.Ordinal));
        _db.Consents.Add(new Domain.Entities.Consent
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            AppId = appId,
            Scope = scope,
            ConsentVersion = version,
            IpAddress = ipAddress,
            UserAgent = userAgent,
            GrantedAt = now,
        });

        bool hasGrant = await _db.AppGrants.AnyAsync(g => g.UserId == userId && g.AppId == appId && g.RevokedAt == null, cancellationToken).ConfigureAwait(false);
        if (!hasGrant)
        {
            _db.AppGrants.Add(new AppGrant { Id = Guid.NewGuid(), UserId = userId, AppId = appId, GrantedAt = now });
        }

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await _audit.WriteAsync(
            new AuditEntry(AuditActions.ConsentGrant, AuditActorType.User, userId, appId, "app", appId,
                Metadata: $"{{\"scope\":\"{scope}\",\"version\":\"{version}\"}}", IpAddress: ipAddress, UserAgent: userAgent),
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task DenyAsync(Guid userId, Guid appId, string? ipAddress, CancellationToken cancellationToken = default)
        => _audit.WriteAsync(new AuditEntry(AuditActions.ConsentDeny, AuditActorType.User, userId, appId, "app", appId, IpAddress: ipAddress), cancellationToken);
}
