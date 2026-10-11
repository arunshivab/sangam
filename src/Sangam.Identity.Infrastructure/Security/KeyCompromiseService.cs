using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Abstractions;
using Sangam.Identity.Application.Abstractions;
using Sangam.Identity.Domain;
using Sangam.Identity.Domain.Enums;
using Sangam.Identity.Infrastructure.Accounts;
using Sangam.Identity.Infrastructure.Persistence;
using Sangam.Identity.Infrastructure.Portal;

namespace Sangam.Identity.Infrastructure.Security;

/// <summary>
/// rc.9 (ASVS V1.6.3; SGM-911 compromise response; <c>docs/security/key-compromise.md</c>): the steps that replace what a
/// compromised key protected. Each is run by the founder on the server, through <see cref="KeyCompromiseCommand"/>, names the
/// incident from the register (SGM-802), and is written to the audit log. Each can be run again safely.
/// </summary>
public sealed class KeyCompromiseService
{
    private const string Revoked = "key_compromise";

    private readonly SangamDbContext _db;
    private readonly IAuditWriter _audit;
    private readonly IClock _clock;
    private readonly IServiceProvider _services;

    /// <summary>Initialises the service.</summary>
    /// <param name="db">Database.</param>
    /// <param name="audit">Audit writer.</param>
    /// <param name="clock">Clock.</param>
    /// <param name="services">Services, for the data-protection key manager and protector.</param>
    public KeyCompromiseService(SangamDbContext db, IAuditWriter audit, IClock clock, IServiceProvider services)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _services = services ?? throw new ArgumentNullException(nameof(services));
    }

    /// <summary>
    /// After the token signing or encryption certificate is compromised: revokes every token (refresh, access, codes) and every
    /// authorization, so every application must sign its people in again.
    /// </summary>
    /// <param name="incident">The incident in the register.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>How many tokens and authorizations were revoked.</returns>
    public async Task<(int Tokens, int Authorizations)> RevokeAllTokensAsync(string incident, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(incident);
        int tokens = await _db.Set<OpenIddict.EntityFrameworkCore.Models.OpenIddictEntityFrameworkCoreToken>()
            .Where(t => t.Status != OpenIddictConstants.Statuses.Revoked)
            .ExecuteUpdateAsync(u => u.SetProperty(t => t.Status, OpenIddictConstants.Statuses.Revoked), cancellationToken)
            .ConfigureAwait(false);
        int authorizations = await _db.Set<OpenIddict.EntityFrameworkCore.Models.OpenIddictEntityFrameworkCoreAuthorization>()
            .Where(a => a.Status != OpenIddictConstants.Statuses.Revoked)
            .ExecuteUpdateAsync(u => u.SetProperty(a => a.Status, OpenIddictConstants.Statuses.Revoked), cancellationToken)
            .ConfigureAwait(false);
        await AuditAsync(AuditActions.SecurityTokensRevokeAll, new Dictionary<string, object> { ["incident"] = incident, ["tokens"] = tokens, ["authorizations"] = authorizations }, cancellationToken).ConfigureAwait(false);
        return (tokens, authorizations);
    }

    /// <summary>
    /// After the key-ring certificate (or the key ring with the database) is compromised: revokes every data-protection key and
    /// makes a new one, protects every authenticator secret again under it, and signs everyone out (all sessions ended, with
    /// back-channel logout to the applications, and every security stamp changed). People keep their authenticators.
    /// </summary>
    /// <param name="incident">The incident in the register.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>How many secrets were protected again, and how many sessions were ended.</returns>
    public async Task<(int Secrets, int Sessions)> RevokeKeyRingAsync(string incident, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(incident);
        IKeyManager keys = _services.GetService<IKeyManager>()
            ?? throw new InvalidOperationException("The data-protection key ring is not persisted here, so there are no keys to revoke.");
        IPersistedDataProtector protector = _services.GetRequiredService<IDataProtectionProvider>().CreateProtector(SangamUserStore.AuthenticatorKeyPurpose) as IPersistedDataProtector
            ?? throw new InvalidOperationException("The data protector cannot read secrets under revoked keys.");

        DateTimeOffset now = _clock.UtcNow;
        keys.RevokeAllKeys(now, "Key compromise: " + incident);
        IKey newKey = keys.CreateNewKey(now, now.AddDays(90));
        await WaitForKeyAsync(protector, newKey.KeyId, cancellationToken).ConfigureAwait(false);
        HashSet<Guid> revoked = [.. keys.GetAllKeys().Where(k => k.IsRevoked).Select(k => k.KeyId)];

        // Read under the revoked key (deliberately: that is what it was protected with) and protect again under the new one.
        // A secret already under a key that is not revoked (a second run) is left as it is.
        List<Microsoft.AspNetCore.Identity.IdentityUserToken<Guid>> secrets = await _db.UserTokens
            .Where(t => t.LoginProvider == SangamUserStore.Provider && t.Name == SangamUserStore.AuthenticatorKeyName && t.Value != null && t.Value.StartsWith(SangamUserStore.ProtectedPrefix))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        int moved = 0;
        foreach (Microsoft.AspNetCore.Identity.IdentityUserToken<Guid> secret in secrets)
        {
            byte[] protectedBytes = WebEncoders.Base64UrlDecode(secret.Value![SangamUserStore.ProtectedPrefix.Length..]);
            if (!revoked.Contains(KeyIdOf(protectedBytes)))
            {
                continue;
            }

            byte[] plain = protector.DangerousUnprotect(protectedBytes, ignoreRevocationErrors: true, out bool _, out bool _);
            secret.Value = SangamUserStore.ProtectedPrefix + WebEncoders.Base64UrlEncode(protector.Protect(plain));
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(plain);
            moved++;
        }

        List<Guid> live = await _db.UserSessions.Where(s => s.RevokedAt == null).Select(s => s.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await _db.UserSessions.Where(s => s.RevokedAt == null)
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.RevokedAt, now).SetProperty(s => s.RevokedReason, Revoked), cancellationToken)
            .ConfigureAwait(false);
        await LogoutQueue.EnqueueAsync(_db, live, now, cancellationToken).ConfigureAwait(false);
        await _db.Users.ExecuteUpdateAsync(u => u.SetProperty(x => x.SecurityStamp, x => Guid.NewGuid().ToString()), cancellationToken).ConfigureAwait(false);

        await AuditAsync(AuditActions.SecurityKeyRingRevoke, new Dictionary<string, object> { ["incident"] = incident, ["secrets"] = moved, ["sessions"] = live.Count }, cancellationToken).ConfigureAwait(false);
        return (moved, live.Count);
    }

    /// <summary>
    /// After the DigiLocker subject key is replaced (the old one moved to <c>PreviousSubjectKeys</c>): asks everyone with a
    /// verified identity to verify again, so their stored hash moves to the new key. The verifications themselves stand.
    /// </summary>
    /// <param name="incident">The incident in the register.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>How many people were asked.</returns>
    public async Task<int> RequestReverificationAsync(string incident, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(incident);
        DateTimeOffset now = _clock.UtcNow;
        int asked = await _db.Users.Where(u => u.IdentityVerifiedAt != null && u.IdentityReverifyRequestedAt == null)
            .ExecuteUpdateAsync(u => u.SetProperty(x => x.IdentityReverifyRequestedAt, now), cancellationToken)
            .ConfigureAwait(false);
        await AuditAsync(AuditActions.SecurityIdentityReverify, new Dictionary<string, object> { ["incident"] = incident, ["people"] = asked }, cancellationToken).ConfigureAwait(false);
        return asked;
    }

    /// <summary>The key a protected payload names: after the 4-byte header, the key id (as Data Protection writes it).</summary>
    private static Guid KeyIdOf(byte[] payload) => payload.Length >= 20 ? new Guid(payload.AsSpan(4, 16)) : Guid.Empty;

    /// <summary>
    /// The key ring is cached and refreshed in the background, so the new key may not be in use at once. Waits (up to 30
    /// seconds) until a fresh protection names it, so no secret is protected again under a revoked key.
    /// </summary>
    private static async Task WaitForKeyAsync(IDataProtector protector, Guid keyId, CancellationToken cancellationToken)
    {
        for (int attempt = 0; attempt < 150; attempt++)
        {
            if (KeyIdOf(protector.Protect([0])) == keyId)
            {
                return;
            }

            await Task.Delay(200, cancellationToken).ConfigureAwait(false);
        }

        throw new InvalidOperationException("The keys are revoked, but the new key did not come into use in time, so no secret was moved. Run the command again; it is safe to repeat.");
    }

    private Task AuditAsync(string action, Dictionary<string, object> metadata, CancellationToken cancellationToken)
        => _audit.WriteAsync(new AuditEntry(action, AuditActorType.System, TargetType: "system", Metadata: JsonSerializer.Serialize(metadata)), cancellationToken);
}
