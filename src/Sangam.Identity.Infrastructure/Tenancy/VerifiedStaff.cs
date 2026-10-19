using Microsoft.EntityFrameworkCore;
using Sangam.Identity.Domain.Entities;
using Sangam.Identity.Infrastructure.Persistence;

namespace Sangam.Identity.Infrastructure.Tenancy;

/// <summary>
/// rc.13 (tenants' request of 14 Oct 2026): whether an organisation takes only people verified with DigiLocker. The setting
/// holds for the organisation it is switched on at and for every organisation under it.
/// </summary>
public static class VerifiedStaff
{
    /// <summary>The message a refused or waiting addition gives.</summary>
    public const string Required = "This organisation takes only people verified with DigiLocker. The person must verify in their Sangam account first.";

    /// <summary>Whether people joining the organisation at <paramref name="path"/> must be verified with DigiLocker.</summary>
    /// <param name="db">Database.</param>
    /// <param name="path">The organisation's materialised path.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><see langword="true"/> when it, or an organisation above it, has the setting on.</returns>
    public static Task<bool> OnAtAsync(SangamDbContext db, string path, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(path);
        List<Guid> ids = [.. OrganisationPath.Ids(path)];
        return db.Organisations.AsNoTracking().AnyAsync(o => ids.Contains(o.Id) && o.VerifiedStaffOnly && o.DeletedAt == null, cancellationToken);
    }

    /// <summary>Whether the person is verified with DigiLocker.</summary>
    /// <param name="db">Database.</param>
    /// <param name="userId">The person.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><see langword="true"/> when verified.</returns>
    public static Task<bool> IsVerifiedAsync(SangamDbContext db, Guid userId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        return db.Users.AsNoTracking().AnyAsync(u => u.Id == userId && u.IdentityVerifiedAt != null, cancellationToken);
    }

    /// <summary>Whether the person may take a new place at the organisation at <paramref name="path"/>.</summary>
    /// <param name="db">Database.</param>
    /// <param name="path">The organisation's materialised path.</param>
    /// <param name="userId">The person.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><see langword="false"/> when the organisation takes only verified people and they are not verified.</returns>
    public static async Task<bool> MayJoinAsync(SangamDbContext db, string path, Guid userId, CancellationToken cancellationToken = default)
        => !await OnAtAsync(db, path, cancellationToken).ConfigureAwait(false) || await IsVerifiedAsync(db, userId, cancellationToken).ConfigureAwait(false);
}
