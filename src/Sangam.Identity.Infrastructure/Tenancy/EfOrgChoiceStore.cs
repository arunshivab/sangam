using Microsoft.EntityFrameworkCore;
using Sangam.Identity.Application.Tenancy;
using Sangam.Identity.Infrastructure.Persistence;

namespace Sangam.Identity.Infrastructure.Tenancy;

/// <summary><see cref="IOrgChoiceStore"/> on the person's link to the application (rc.12, A6).</summary>
public sealed class EfOrgChoiceStore : IOrgChoiceStore
{
    private readonly SangamDbContext _db;

    /// <summary>Initialises the store.</summary>
    /// <param name="db">Database.</param>
    public EfOrgChoiceStore(SangamDbContext db)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    /// <inheritdoc />
    public Task<Guid?> LastAsync(Guid userId, Guid appId, CancellationToken cancellationToken = default)
        => _db.AppGrants.AsNoTracking()
            .Where(g => g.UserId == userId && g.AppId == appId && g.RevokedAt == null)
            .Select(g => g.LastOrgId)
            .FirstOrDefaultAsync(cancellationToken);

    /// <inheritdoc />
    public async Task RememberAsync(Guid userId, Guid appId, Guid orgId, CancellationToken cancellationToken = default)
    {
        // Before the person has consented there is no link yet; the choice is still made, just not listed first next time.
        await _db.AppGrants
            .Where(g => g.UserId == userId && g.AppId == appId && g.RevokedAt == null && g.LastOrgId != orgId)
            .ExecuteUpdateAsync(s => s.SetProperty(g => g.LastOrgId, orgId), cancellationToken).ConfigureAwait(false);
    }
}
