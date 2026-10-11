using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Sangam.Identity.Domain.Entities;
using Sangam.Identity.Infrastructure.Persistence;
using Sangam.Identity.Infrastructure.Security;

namespace Sangam.Identity.Infrastructure.Accounts;

/// <summary>
/// R7 (ASVS V2.8.2, V2.6.2): the Identity user store, with the two-step secrets kept safe in the database.
/// <list type="bullet">
/// <item>The authenticator app's shared secret is encrypted with ASP.NET Data Protection (the key ring every Sangam host
/// shares), so a copy of the database alone does not let anyone generate a person's codes.</item>
/// <item>Recovery codes are kept only as hashes; redeeming one compares hashes. From rc.8 (V2.6.2) the hash is an
/// HMAC-SHA-256 keyed with the password pepper and naming its version (<c>h2:&lt;version&gt;:</c>), so a copy of the
/// database alone cannot be searched for codes; the R7 form (unkeyed, <c>h1:</c>) is still accepted.</item>
/// </list>
/// Values written before R7 (a plain secret, plain recovery codes) are still read, and replaced with the protected form
/// the next time they are written.
/// </summary>
public sealed class SangamUserStore : UserOnlyStore<SangamUser, SangamDbContext, Guid>
{
    /// <summary>The prefix of a protected authenticator secret.</summary>
    public const string ProtectedPrefix = "dp1:";

    /// <summary>The prefix of a recovery code hashed without a key (R7).</summary>
    public const string HashedPrefix = "h1:";

    /// <summary>The prefix of a recovery code hashed with the pepper (rc.8).</summary>
    public const string KeyedPrefix = "h2:";

    private const string Provider = "[AspNetUserStore]";
    private const string AuthenticatorKeyName = "AuthenticatorKey";
    private const string RecoveryCodesName = "RecoveryCodes";

    private readonly IDataProtector _protector;
    private readonly Dictionary<int, byte[]> _peppers = [];
    private readonly int _pepperVersion;

    /// <summary>Initialises the store.</summary>
    /// <param name="context">Database.</param>
    /// <param name="protection">Data protection.</param>
    /// <param name="hashing">Password hashing settings, for the pepper that keys recovery-code hashes (rc.8).</param>
    /// <param name="describer">Error describer.</param>
    public SangamUserStore(SangamDbContext context, IDataProtectionProvider protection, Argon2idOptions hashing, IdentityErrorDescriber? describer = null)
        : base(context, describer)
    {
        ArgumentNullException.ThrowIfNull(protection);
        ArgumentNullException.ThrowIfNull(hashing);
        _protector = protection.CreateProtector("Sangam.Identity.AuthenticatorKey.v1");
        foreach ((int version, string value) in hashing.PreviousPeppers)
        {
            if (PasswordPepperGuard.Decode(value) is byte[] previous)
            {
                _peppers[version] = previous;
            }
        }

        if (PasswordPepperGuard.Decode(hashing.Pepper) is byte[] current)
        {
            _peppers[hashing.PepperVersion] = current;
            _pepperVersion = hashing.PepperVersion;
        }
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
        return SetTokenAsync(user, Provider, RecoveryCodesName, string.Join(';', recoveryCodes.Select(Store)), cancellationToken);
    }

    /// <inheritdoc />
    public override async Task<bool> RedeemCodeAsync(SangamUser user, string code, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(code);
        string stored = await GetTokenAsync(user, Provider, RecoveryCodesName, cancellationToken).ConfigureAwait(false) ?? string.Empty;
        List<string> codes = [.. stored.Split(';', StringSplitOptions.RemoveEmptyEntries)];
        string hashed = Hash(code);
        int at = codes.FindIndex(c => Matches(c, code, hashed));
        if (at < 0)
        {
            return false;
        }

        codes.RemoveAt(at);
        await SetTokenAsync(user, Provider, RecoveryCodesName, string.Join(';', codes.Select(c => c.StartsWith(HashedPrefix, StringComparison.Ordinal) || c.StartsWith(KeyedPrefix, StringComparison.Ordinal) ? c : Hash(c))), cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>The stored form of a new code: keyed with the pepper when there is one (Development and Testing have none).</summary>
    private string Store(string code)
        => _peppers.TryGetValue(_pepperVersion, out byte[]? pepper) && _pepperVersion > 0
            ? KeyedPrefix + _pepperVersion.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + Keyed(pepper, code)
            : Hash(code);

    private bool Matches(string stored, string code, string hashed)
    {
        if (stored.StartsWith(KeyedPrefix, StringComparison.Ordinal))
        {
            string[] parts = stored[KeyedPrefix.Length..].Split(':', 2);
            return parts.Length == 2
                && int.TryParse(parts[0], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int version)
                && _peppers.TryGetValue(version, out byte[]? pepper)
                && FixedEquals(parts[1], Keyed(pepper, code));
        }

        return FixedEquals(stored, hashed) || (!stored.StartsWith(HashedPrefix, StringComparison.Ordinal) && FixedEquals(stored, code));
    }

    // Keyed codes are compared without dashes, spaces or case, so "ABCD EFGH…" and "abcd-efgh-…" are the same code.
    private static string Keyed(byte[] pepper, string code)
        => Convert.ToBase64String(HMACSHA256.HashData(pepper, Encoding.UTF8.GetBytes(Canonical(code))));

    private static string Canonical(string code)
        => new([.. code.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant)]);

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
