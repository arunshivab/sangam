using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenIddict.Abstractions;
using OpenIddict.Core;
using Sangam.Identity.Application.Abstractions;
using Sangam.Identity.Domain.Entities;
using Sangam.Identity.Infrastructure.Persistence;

namespace Sangam.Identity.Infrastructure.Apps;

/// <summary>
/// rc.11 (R5; founder decision, 13 Oct 2026): OpenIddict's application manager, with two live client secrets. A confidential
/// application's secret is accepted when it is either
/// <list type="bullet">
/// <item>the secret Sangam issued from its configuration (OpenIddict's own column), unless the application's owner retired
/// it; or</item>
/// <item>a secret the owner made on the partner console that is neither revoked nor past its twelve months.</item>
/// </list>
/// So the owner makes a new secret, moves their servers to it, then revokes the old one: no break in sign-in. A disabled
/// application's secrets are all refused. Everything else is OpenIddict's own behaviour, including how secrets are hashed
/// and compared.
/// </summary>
/// <typeparam name="TApplication">OpenIddict's application entity.</typeparam>
public class SangamApplicationManager<TApplication> : OpenIddictApplicationManager<TApplication>
    where TApplication : class
{
    private static readonly TimeSpan UseResolution = TimeSpan.FromHours(1);

    private readonly SangamDbContext _db;
    private readonly IClock _clock;

    /// <summary>Initialises the manager.</summary>
    /// <param name="cache">OpenIddict's application cache.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="options">OpenIddict's core options.</param>
    /// <param name="store">OpenIddict's application store.</param>
    /// <param name="db">Database, for the secrets owners made.</param>
    /// <param name="clock">Clock.</param>
    public SangamApplicationManager(
        IOpenIddictApplicationCache<TApplication> cache,
        ILogger<OpenIddictApplicationManager<TApplication>> logger,
        IOptionsMonitor<OpenIddictCoreOptions> options,
        IOpenIddictApplicationStore<TApplication> store,
        SangamDbContext db,
        IClock clock)
        : base(cache, logger, options, store)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <inheritdoc />
    public override async ValueTask<bool> ValidateClientSecretAsync(TApplication application, string secret, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(application);
        if (string.IsNullOrEmpty(secret))
        {
            return false;
        }

        string? clientId = await GetClientIdAsync(application, cancellationToken).ConfigureAwait(false);
        var app = clientId is null ? null : await _db.Apps.AsNoTracking()
            .Where(a => a.ClientId == clientId)
            .Select(a => new { a.Id, a.IssuedSecretRetiredAt, a.Status })
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        // rc.11: a disabled application authenticates with no secret at all, so disabling it also stops client-credentials
        // tokens (an operator's emergency stop when its owner cannot be reached to revoke a leaked secret).
        if (app is not null && app.Status != Domain.Enums.AppStatus.Active)
        {
            return false;
        }

        // The secret Sangam issued: OpenIddict's own check, unless the owner retired it.
        if (app?.IssuedSecretRetiredAt is null && await base.ValidateClientSecretAsync(application, secret, cancellationToken).ConfigureAwait(false))
        {
            return true;
        }

        if (app is null || !await HasClientTypeAsync(application, OpenIddictConstants.ClientTypes.Confidential, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        DateTimeOffset now = _clock.UtcNow;
        List<ClientSecret> live = await _db.ClientSecrets
            .Where(s => s.AppId == app.Id && s.RevokedAt == null && s.ExpiresAt > now)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (ClientSecret candidate in live)
        {
            if (await ValidateClientSecretAsync(secret, candidate.Hash, cancellationToken).ConfigureAwait(false))
            {
                if (candidate.LastUsedAt is null || now - candidate.LastUsedAt > UseResolution)
                {
                    candidate.LastUsedAt = now;
                    await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                }

                return true;
            }
        }

        return false;
    }

    /// <summary>Whether <paramref name="secret"/> is the secret Sangam issued from its configuration (OpenIddict's own column only).</summary>
    /// <param name="application">The application.</param>
    /// <param name="secret">The secret to check.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public ValueTask<bool> IsIssuedSecretAsync(TApplication application, string secret, CancellationToken cancellationToken = default)
        => base.ValidateClientSecretAsync(application, secret, cancellationToken);

    /// <summary>Hashes a new secret the way OpenIddict hashes its own.</summary>
    /// <param name="secret">The secret.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public ValueTask<string> HashSecretAsync(string secret, CancellationToken cancellationToken = default)
        => ObfuscateClientSecretAsync(secret, cancellationToken);
}
