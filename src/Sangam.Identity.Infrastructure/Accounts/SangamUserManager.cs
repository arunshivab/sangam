using System.Security.Cryptography;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sangam.Identity.Domain.Entities;

namespace Sangam.Identity.Infrastructure.Accounts;

/// <summary>
/// rc.8 (ASVS V2.6.2, V2.6.3): the Identity user manager, with recovery codes long enough to stand on their own —
/// 24 characters from a 32-letter lower-case alphabet (120 bits), shown in groups of four so they cannot be misread
/// (SGM-913 B3). Everything else is the framework's own.
/// </summary>
public sealed class SangamUserManager : UserManager<SangamUser>
{
    /// <summary>Characters in a recovery code, without the dashes.</summary>
    public const int RecoveryCodeLength = 24;

    // Lower-case letters and digits without 0, 1, l and o, which are easily misread: 32 symbols, 5 bits each.
    private const string Alphabet = "abcdefghijkmnpqrstuvwxyz23456789";

    /// <summary>Initialises the manager (the framework's constructor).</summary>
    /// <param name="store">User store.</param>
    /// <param name="optionsAccessor">Identity options.</param>
    /// <param name="passwordHasher">Password hasher.</param>
    /// <param name="userValidators">User validators.</param>
    /// <param name="passwordValidators">Password validators.</param>
    /// <param name="keyNormalizer">Key normaliser.</param>
    /// <param name="errors">Error describer.</param>
    /// <param name="services">Services.</param>
    /// <param name="logger">Logger.</param>
    public SangamUserManager(
        IUserStore<SangamUser> store,
        IOptions<IdentityOptions> optionsAccessor,
        IPasswordHasher<SangamUser> passwordHasher,
        IEnumerable<IUserValidator<SangamUser>> userValidators,
        IEnumerable<IPasswordValidator<SangamUser>> passwordValidators,
        ILookupNormalizer keyNormalizer,
        IdentityErrorDescriber errors,
        IServiceProvider services,
        ILogger<UserManager<SangamUser>> logger)
        : base(store, optionsAccessor, passwordHasher, userValidators, passwordValidators, keyNormalizer, errors, services, logger)
    {
    }

    /// <summary>A new recovery code: 24 random characters, grouped <c>abcd-efgh-…</c>.</summary>
    protected override string CreateTwoFactorRecoveryCode() => NewRecoveryCode();

    /// <summary>A new recovery code (exposed for tests).</summary>
    public static string NewRecoveryCode()
    {
        char[] chars = new char[RecoveryCodeLength];
        for (int i = 0; i < chars.Length; i++)
        {
            chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        }

        return string.Join('-', Enumerable.Range(0, RecoveryCodeLength / 4).Select(g => new string(chars, g * 4, 4)));
    }
}
