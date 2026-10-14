using Microsoft.EntityFrameworkCore;
using Sangam.Identity.Application.Abstractions;
using Sangam.Identity.Domain.Entities;
using Sangam.Identity.Infrastructure.Persistence;

namespace Sangam.Identity.Infrastructure.Tenancy;

/// <summary>
/// rc.12: who may manage staff where. An application admin manages every organisation of their application; a customer or
/// branch admin (<see cref="OrgAdmin"/>) manages the organisation their right is at and everything below it. Adding people
/// (or linking an account) also needs the admin to be verified with DigiLocker, unless a Sangam operator has switched on the
/// application's temporary exception.
/// </summary>
public sealed class StaffRights
{
    private readonly SangamDbContext _db;
    private readonly IClock _clock;

    /// <summary>Initialises the rules.</summary>
    /// <param name="db">Database.</param>
    /// <param name="clock">Clock.</param>
    public StaffRights(SangamDbContext db, IClock clock)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <summary>The live, active organisation of the application, or <see langword="null"/>.</summary>
    /// <param name="appId">The application.</param>
    /// <param name="orgId">The organisation.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The organisation.</returns>
    public Task<Organisation?> OrganisationAsync(Guid appId, Guid orgId, CancellationToken cancellationToken = default)
        => _db.Organisations.AsNoTracking().FirstOrDefaultAsync(o => o.Id == orgId && o.RegisteredViaAppId == appId && o.DeletedAt == null, cancellationToken);

    /// <summary>Whether the person administers the organisation in the application.</summary>
    /// <param name="appId">The application.</param>
    /// <param name="org">The organisation.</param>
    /// <param name="userId">The person.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><see langword="true"/> when they do.</returns>
    public async Task<bool> ManagesAsync(Guid appId, Organisation org, Guid userId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(org);
        if (await _db.AppAdmins.AnyAsync(a => a.AppId == appId && a.UserId == userId && a.RevokedAt == null, cancellationToken).ConfigureAwait(false))
        {
            return true;
        }

        List<Guid> covering = [.. OrganisationPath.Ids(org.Path)];
        return await _db.OrgAdmins.AnyAsync(
            a => a.AppId == appId && a.UserId == userId && a.RevokedAt == null && a.Right == OrgAdminRight.Admin && covering.Contains(a.OrgId),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Whether the person may add people (decision 2 of 14 Oct 2026): verified with DigiLocker, or not yet verified while the
    /// application's temporary exception is on (then each addition is marked "added before DigiLocker").
    /// </summary>
    /// <param name="appId">The application.</param>
    /// <param name="userId">The admin.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Whether they may, and whether only under the exception.</returns>
    public async Task<(bool May, bool BeforeDigiLocker)> MayAddAsync(Guid appId, Guid userId, CancellationToken cancellationToken = default)
    {
        bool verified = await _db.Users.AnyAsync(u => u.Id == userId && u.IdentityVerifiedAt != null, cancellationToken).ConfigureAwait(false);
        if (verified)
        {
            return (true, false);
        }

        DateTimeOffset now = _clock.UtcNow;
        bool exception = await _db.Apps.AnyAsync(a => a.Id == appId && a.AddWithoutDigiLockerUntil != null && a.AddWithoutDigiLockerUntil > now, cancellationToken).ConfigureAwait(false);
        return (exception, exception);
    }
}
