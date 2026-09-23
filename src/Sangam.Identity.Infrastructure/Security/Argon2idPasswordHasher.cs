using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;
using Microsoft.AspNetCore.Identity;

namespace Sangam.Identity.Infrastructure.Security;

/// <summary>
/// Argon2id password hashing for ASP.NET Core Identity, replacing the PBKDF2 default.
/// Stores the PHC string format <c>$argon2id$v=19$m=65536,t=3,p=1$&lt;salt&gt;$&lt;hash&gt;</c>
/// (base64 without padding), so parameters travel with the hash and can be raised later:
/// a hash made with weaker parameters verifies but reports
/// <see cref="PasswordVerificationResult.SuccessRehashNeeded"/>.
/// Defaults exceed the OWASP minimum (m=19 MiB, t=2, p=1).
/// </summary>
/// <typeparam name="TUser">The user type.</typeparam>
public sealed class Argon2idPasswordHasher<TUser> : IPasswordHasher<TUser>
    where TUser : class
{
    private const string Prefix = "$argon2id$v=19$";
    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    private readonly Argon2idOptions _options;

    /// <summary>Initialises the hasher with the given parameters.</summary>
    /// <param name="options">Cost parameters; <see langword="null"/> selects the defaults.</param>
    public Argon2idPasswordHasher(Argon2idOptions? options = null)
    {
        _options = options ?? new Argon2idOptions();
    }

    /// <inheritdoc />
    public string HashPassword(TUser user, string password)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentException.ThrowIfNullOrEmpty(password);

        byte[] salt = RandomNumberGenerator.GetBytes(SaltBytes);
        byte[] hash = Derive(password, salt, _options.MemoryKiB, _options.Iterations, _options.Parallelism);

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{Prefix}m={_options.MemoryKiB},t={_options.Iterations},p={_options.Parallelism}${ToBase64(salt)}${ToBase64(hash)}");
    }

    /// <inheritdoc />
    public PasswordVerificationResult VerifyHashedPassword(TUser user, string hashedPassword, string providedPassword)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(hashedPassword);
        ArgumentNullException.ThrowIfNull(providedPassword);

        if (!TryParse(hashedPassword, out int memory, out int iterations, out int parallelism, out byte[] salt, out byte[] expected))
        {
            return PasswordVerificationResult.Failed;
        }

        byte[] actual = Derive(providedPassword, salt, memory, iterations, parallelism);
        if (!CryptographicOperations.FixedTimeEquals(actual, expected))
        {
            return PasswordVerificationResult.Failed;
        }

        bool weaker = memory < _options.MemoryKiB || iterations < _options.Iterations || parallelism < _options.Parallelism;
        return weaker ? PasswordVerificationResult.SuccessRehashNeeded : PasswordVerificationResult.Success;
    }

    private static byte[] Derive(string password, byte[] salt, int memoryKiB, int iterations, int parallelism)
    {
        using Argon2id argon = new(Encoding.UTF8.GetBytes(password))
        {
            Salt = salt,
            MemorySize = memoryKiB,
            Iterations = iterations,
            DegreeOfParallelism = parallelism,
        };
        return argon.GetBytes(HashBytes);
    }

    private static bool TryParse(string encoded, out int memory, out int iterations, out int parallelism, out byte[] salt, out byte[] hash)
    {
        memory = 0;
        iterations = 0;
        parallelism = 0;
        salt = [];
        hash = [];

        if (!encoded.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        string[] parts = encoded[Prefix.Length..].Split('$');
        if (parts.Length != 3)
        {
            return false;
        }

        foreach (string kv in parts[0].Split(','))
        {
            string[] pair = kv.Split('=');
            if (pair.Length != 2 || !int.TryParse(pair[1], NumberStyles.None, CultureInfo.InvariantCulture, out int value))
            {
                return false;
            }

            switch (pair[0])
            {
                case "m":
                    memory = value;
                    break;
                case "t":
                    iterations = value;
                    break;
                case "p":
                    parallelism = value;
                    break;
                default:
                    return false;
            }
        }

        if (memory <= 0 || iterations <= 0 || parallelism <= 0)
        {
            return false;
        }

        try
        {
            salt = FromBase64(parts[1]);
            hash = FromBase64(parts[2]);
        }
        catch (FormatException)
        {
            return false;
        }

        return salt.Length > 0 && hash.Length > 0;
    }

    private static string ToBase64(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=');

    private static byte[] FromBase64(string s)
    {
        int pad = (4 - (s.Length % 4)) % 4;
        return Convert.FromBase64String(s + new string('=', pad));
    }
}
