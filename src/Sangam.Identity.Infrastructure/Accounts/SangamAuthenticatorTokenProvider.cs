using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Sangam.Identity.Application.Abstractions;
using Sangam.Identity.Domain.Entities;
using Sangam.Identity.Infrastructure.Persistence;

namespace Sangam.Identity.Infrastructure.Accounts;

/// <summary>
/// R7 (ASVS V2.8.4): authenticator-app codes (RFC 6238: HMAC-SHA1, 30-second steps, 6 digits) that work once. The
/// framework's provider accepted the same code again for as long as it was valid; this one records, per person, the
/// last time step it accepted, with one conditional upsert, so a code already used — even by a request racing this one —
/// is refused. A code from the step before or after the current one is accepted, for clocks a little out.
/// </summary>
public sealed class SangamAuthenticatorTokenProvider : IUserTwoFactorTokenProvider<SangamUser>
{
    /// <summary>The <c>user_tokens</c> provider and name that hold the last accepted step.</summary>
    public const string StepProvider = "[Sangam]";

    /// <summary>The <c>user_tokens</c> name.</summary>
    public const string StepName = "LastTotpStep";

    private readonly SangamDbContext _db;
    private readonly IClock _clock;

    /// <summary>Initialises the provider.</summary>
    /// <param name="db">Database.</param>
    /// <param name="clock">Clock.</param>
    public SangamAuthenticatorTokenProvider(SangamDbContext db, IClock clock)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <inheritdoc />
    public async Task<bool> CanGenerateTwoFactorTokenAsync(UserManager<SangamUser> manager, SangamUser user)
    {
        ArgumentNullException.ThrowIfNull(manager);
        return !string.IsNullOrWhiteSpace(await manager.GetAuthenticatorKeyAsync(user).ConfigureAwait(false));
    }

    /// <inheritdoc />
    public Task<string> GenerateAsync(string purpose, UserManager<SangamUser> manager, SangamUser user) => Task.FromResult(string.Empty);

    /// <inheritdoc />
    public async Task<bool> ValidateAsync(string purpose, string token, UserManager<SangamUser> manager, SangamUser user)
    {
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(user);
        string? key = await manager.GetAuthenticatorKeyAsync(user).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(key) || token is null || token.Length != 6 || !token.All(char.IsAsciiDigit) || Base32Decode(key) is not byte[] secret)
        {
            return false;
        }

        long now = _clock.UtcNow.ToUnixTimeSeconds() / 30;
        long? matched = null;
        for (long step = now - 1; step <= now + 1; step++)
        {
            if (CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(Code(secret, step)), System.Text.Encoding.ASCII.GetBytes(token)))
            {
                matched = step;
            }
        }

        if (matched is not long accepted)
        {
            return false;
        }

        // Accepted only if no later or equal step was accepted before: the row is written only when it moves forward.
        int moved = await _db.Database.ExecuteSqlRawAsync(
            "INSERT INTO user_tokens (user_id, login_provider, name, value) VALUES (@user, @provider, @name, @step) " +
            "ON CONFLICT (user_id, login_provider, name) DO UPDATE SET value = excluded.value " +
            "WHERE CAST(user_tokens.value AS bigint) < CAST(excluded.value AS bigint)",
            [
                new NpgsqlParameter("user", user.Id),
                new NpgsqlParameter("provider", StepProvider),
                new NpgsqlParameter("name", StepName),
                new NpgsqlParameter("step", accepted.ToString(CultureInfo.InvariantCulture)),
            ]).ConfigureAwait(false);
        return moved == 1;
    }

    /// <summary>The 6-digit code for a time step (RFC 6238 over RFC 4226).</summary>
    /// <param name="secret">The shared secret.</param>
    /// <param name="step">The 30-second step since 1970.</param>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5350:Do Not Use Weak Cryptographic Algorithms", Justification = "RFC 6238 and every authenticator app use HMAC-SHA1; SHA-1's collision weakness does not affect HMAC.")]
    public static string Code(byte[] secret, long step)
    {
        ArgumentNullException.ThrowIfNull(secret);
        Span<byte> counter = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(counter, step);
        byte[] hash = HMACSHA1.HashData(secret, counter);
        int offset = hash[^1] & 0x0F;
        int binary = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (binary % 1_000_000).ToString("D6", CultureInfo.InvariantCulture);
    }

    /// <summary>Decodes an RFC 4648 Base32 secret (case and spaces ignored, padding optional), or returns
    /// <see langword="null"/>.</summary>
    /// <param name="text">The secret as the person's app shows it.</param>
    public static byte[]? Base32Decode(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        List<byte> bytes = [];
        int buffer = 0;
        int bits = 0;
        foreach (char c in text.ToUpperInvariant())
        {
            if (c is ' ' or '=' or '-')
            {
                continue;
            }

            int value = Alphabet.IndexOf(c, StringComparison.Ordinal);
            if (value < 0)
            {
                return null;
            }

            buffer = (buffer << 5) | value;
            bits += 5;
            if (bits >= 8)
            {
                bits -= 8;
                bytes.Add((byte)(buffer >> bits));
                buffer &= (1 << bits) - 1;
            }
        }

        return bytes.Count == 0 ? null : [.. bytes];
    }
}
