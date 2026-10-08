using Microsoft.EntityFrameworkCore;
using Sangam.Identity.Application.Security;
using Sangam.Identity.Domain;
using Sangam.Identity.Domain.Entities;
using Sangam.Identity.Infrastructure.Persistence;

namespace Sangam.Identity.Infrastructure.Policies;

/// <summary>
/// <see cref="ISecurityPolicyService"/> on the database. The application's sign-in rule replaces the platform's
/// (the platform operators set it, and may set a weaker one, D-092); everything else — and every organisation level — only tightens.
/// </summary>
public sealed class EfSecurityPolicyService : ISecurityPolicyService
{
    private readonly SangamDbContext _db;
    private readonly PolicySettings _settings;
    private readonly IBreachedPasswordChecker _breaches;

    /// <summary>Initialises the service.</summary>
    /// <param name="db">Database.</param>
    /// <param name="settings">Platform settings.</param>
    /// <param name="breaches">The breached-password check, to say whether it is available (D-J).</param>
    public EfSecurityPolicyService(SangamDbContext db, PolicySettings settings, IBreachedPasswordChecker breaches)
    {
        _breaches = breaches ?? throw new ArgumentNullException(nameof(breaches));
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    /// <inheritdoc />
    public SecurityPolicy Platform => _settings.Platform;

    /// <inheritdoc />
    public bool BreachCheckAvailable => _breaches.Available;

    /// <inheritdoc />
    public async Task<SecurityPolicy> ForAppAsync(Guid? appId, CancellationToken cancellationToken = default)
    {
        if (appId is not Guid id)
        {
            return Platform;
        }

        App? app = await _db.Apps.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id, cancellationToken).ConfigureAwait(false);
        return app is null ? Platform : ForApp(app);
    }

    /// <inheritdoc />
    public async Task<PersonPolicy> ForPersonAsync(Guid userId, Guid? appId, CancellationToken cancellationToken = default)
    {
        SecurityPolicy appPolicy = await ForAppAsync(appId, cancellationToken).ConfigureAwait(false);
        if (appId is not Guid id)
        {
            return new PersonPolicy(appPolicy, false);
        }

        List<string> paths = await _db.OrgMemberships.AsNoTracking()
            .Where(m => m.UserId == userId && m.AppId == id && m.RevokedAt == null)
            .Join(_db.Organisations.Where(o => o.DeletedAt == null), m => m.OrgId, o => o.Id, (m, o) => o.Path)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        SecurityPolicy policy = appPolicy;
        if (paths.Count > 0)
        {
            List<Guid> ids = [.. paths.SelectMany(OrganisationPath.Ids).Distinct()];
            Dictionary<Guid, Organisation> orgs = await _db.Organisations.AsNoTracking()
                .Where(o => ids.Contains(o.Id))
                .ToDictionaryAsync(o => o.Id, cancellationToken)
                .ConfigureAwait(false);

            foreach (string path in paths)
            {
                SecurityPolicy chain = appPolicy;
                foreach (Guid orgId in OrganisationPath.Ids(path))
                {
                    if (orgs.TryGetValue(orgId, out Organisation? org))
                    {
                        chain = chain.Tighten(org.SignInPolicy, org.MinPasswordLength, org.MfaRequirement, org.BreachedPasswordCheck);
                    }
                }

                policy = policy.Strictest(chain);
            }
        }

        bool administrator = await _db.AppAdmins.AnyAsync(a => a.AppId == id && a.UserId == userId && a.RevokedAt == null, cancellationToken).ConfigureAwait(false);
        return new PersonPolicy(policy, administrator);
    }

    /// <inheritdoc />
    public async Task<SecurityPolicy> ForPasswordAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        List<Guid> appIds = await _db.AppGrants.AsNoTracking().Where(g => g.UserId == userId && g.RevokedAt == null).Select(g => g.AppId)
            .Union(_db.OrgMemberships.AsNoTracking().Where(m => m.UserId == userId && m.RevokedAt == null).Select(m => m.AppId))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        SecurityPolicy policy = Platform;
        foreach (Guid appId in appIds)
        {
            policy = policy.Strictest((await ForPersonAsync(userId, appId, cancellationToken).ConfigureAwait(false)).Policy);
        }

        return policy;
    }

    private SecurityPolicy ForApp(App app)
        => (Platform with { SignIn = app.SignInPolicy }).Tighten(null, app.MinPasswordLength, app.MfaRequirement, app.BreachedPasswordCheck);
}
