using Microsoft.EntityFrameworkCore;
using Sangam.Identity.Application.Apps;
using Sangam.Identity.Domain.Entities;
using Sangam.Identity.Infrastructure.Persistence;

namespace Sangam.Identity.Infrastructure.Apps;

/// <summary><see cref="IAppDirectory"/> over the <c>apps</c> table.</summary>
public sealed class EfAppDirectory : IAppDirectory
{
    private readonly SangamDbContext _db;

    /// <summary>Initialises the directory.</summary>
    /// <param name="db">Database.</param>
    public EfAppDirectory(SangamDbContext db)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    /// <inheritdoc />
    public async Task<AppSummary?> FindByClientIdAsync(string clientId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(clientId);
        App? app = await _db.Apps.AsNoTracking().FirstOrDefaultAsync(a => a.ClientId == clientId, cancellationToken).ConfigureAwait(false);
        return app is null ? null : ToSummary(app);
    }

    /// <inheritdoc />
    public async Task<AppSummary?> FindByIdAsync(Guid appId, CancellationToken cancellationToken = default)
    {
        App? app = await _db.Apps.AsNoTracking().FirstOrDefaultAsync(a => a.Id == appId, cancellationToken).ConfigureAwait(false);
        return app is null ? null : ToSummary(app);
    }

    internal static AppSummary ToSummary(App a) => new(
        a.Id, a.ClientId, a.Slug, a.DisplayName, a.OwnerCompanyName, a.Description, a.PrivacyUrl, a.TermsUrl,
        a.BrandColour, a.Glyph, a.SignInPolicy, a.ConsentVersion, a.Status);
}
