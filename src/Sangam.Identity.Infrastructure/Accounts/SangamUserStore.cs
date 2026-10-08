using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Sangam.Identity.Domain.Entities;
using Sangam.Identity.Infrastructure.Persistence;

namespace Sangam.Identity.Infrastructure.Accounts;

/// <summary>
/// R7 (ASVS V2.8.2, V2.6.2): the Identity user store, with the two-step secrets kept safe in the database.
/// <list type="bullet">
/// <item>The authenticator app's shared secret is encrypted with ASP.NET Data Protection (the key ring every Sangam host
/// shares), so a copy of the database alone does not let anyone generate a person's codes.</item>
/// <item>Recovery codes are kept only as SHA-256 hashes; redeeming one compares hashes.</item>
/// </list>
/// Values written before R7 (a plain secret, plain recovery codes) are still read, and replaced with the protected form
/// the next time they are written.
/// </summary>
public sealed class SangamUserStore : UserOnlyStore<SangamUser, SangamDbContext, Guid>
{
    /// <summary>The prefix of a protected authenticator secret.</summary>
    public const string ProtectedPrefix = "dp1:";

    /// <summary>The prefix of a hashed recovery code.</summary>
    public const string HashedPrefix = "h1:";

    private const string Provider = "[AspNetUserStore]";
    private const string AuthenticatorKeyName = "AuthenticatorKey";
    private const string RecoveryCodesName = "RecoveryCodes";

    private readonly IDataProtector _protector;

    /// <summary>Initialises the store.</summary>
    /// <param name="context">Database.</param>
    /// <param name="protection">Data protection.</param>
    /// <param name="describer">Error describer.</param>
    public SangamUserStore(SangamDbContext context, IDataProtectionProvider protection, IdentityErrorDescriber? describer = null)
        : base(context, describer)
    {
        ArgumentNullException.ThrowIfNull(protection);
        _protector = protection.CreateProtector("Sangam.Identity.AuthenticatorKey.v1");
    }

    /// <inheritdoc />
    public override async Task SetAuthenticatorKeyAsync(SangamUser user, string key, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        await SetTokenAsync(user, Provider, AuthenticatorKeyName, ProtectedPrefix + _protector.Protect(key), cancellationToken).ConfigureAwait(false);

        // A new secret starts afresh: the last step accepted under the old one does not hold the new one back.
        await RemoveTokenAsync(user, SangamAuthenticatorTokenProvider.StepProvider, SangamAuthenticatorTokenProvider.StepName, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override async Task<string?> GetAuthenticatorKeyAsync(SangamUser user, CancellationToken cancellationToken)
    {
        string? stored = await GetTokenAsync(user, Provider, AuthenticatorKeyName, cancellationToken).ConfigureAwait(false);
        return stored is not null && stored.StartsWith(ProtectedPrefix, StringComparison.Ordinal) ? _protector.Unprotect(stored[ProtectedPrefix.Length..]) : stored;
    }

    /// <inheritdoc />
    public override Task ReplaceCodesAsync(SangamUser user, IEnumerable<string> recoveryCodes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(recoveryCodes);
        return SetTokenAsync(user, Provider, RecoveryCodesName, string.Join(';', recoveryCodes.Select(Hash)), cancellationToken);
    }

    /// <inheritdoc />
    public override async Task<bool> RedeemCodeAsync(SangamUser user, string code, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(code);
        string stored = await GetTokenAsync(user, Provider, RecoveryCodesName, cancellationToken).ConfigureAwait(false) ?? string.Empty;
        List<string> codes = [.. stored.Split(';', StringSplitOptions.RemoveEmptyEntries)];
        string hashed = Hash(code);
        int at = codes.FindIndex(c => FixedEquals(c, hashed) || (!c.StartsWith(HashedPrefix, StringComparison.Ordinal) && FixedEquals(c, code)));
        if (at < 0)
        {
            return false;
        }

        codes.RemoveAt(at);
        await SetTokenAsync(user, Provider, RecoveryCodesName, string.Join(';', codes.Select(c => c.StartsWith(HashedPrefix, StringComparison.Ordinal) ? c : Hash(c))), cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>The stored form of a recovery code.</summary>
    /// <param name="code">The code.</param>
    public static string Hash(string code)
    {
        ArgumentNullException.ThrowIfNull(code);
        return HashedPrefix + Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(code.Trim())));
    }

    private static bool FixedEquals(string a, string b)
        => CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
}
