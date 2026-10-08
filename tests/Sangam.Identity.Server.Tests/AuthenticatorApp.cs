using System.Security.Cryptography;
using Microsoft.AspNetCore.Identity;
using Sangam.Identity.Domain.Entities;

namespace Sangam.Identity.Server.Tests;

/// <summary>
/// Produces the code an authenticator app on a phone would show. Identity's own provider will
/// not generate one — its <c>GenerateAsync</c> returns an empty string on purpose, since only the
/// phone is meant to hold the ability to produce codes — so tests compute it from the stored key.
/// </summary>
internal static class AuthenticatorApp
{
    /// <param name="users">User manager.</param>
    /// <param name="user">The person.</param>
    /// <param name="ahead">How far ahead: a code works once (R7), so a second sign-in in the same 30 seconds uses the
    /// next one, as the phone would show it a moment later.</param>
    public static async Task<string> CurrentCodeAsync(UserManager<SangamUser> users, SangamUser user, TimeSpan ahead = default)
    {
        string key = (await users.GetAuthenticatorKeyAsync(user))!;
        long step = (DateTimeOffset.UtcNow + ahead).ToUnixTimeSeconds() / 30;
        byte[] counter = BitConverter.GetBytes(step);
        if (BitConverter.IsLittleEndian)
        {
            Array.Reverse(counter);
        }

#pragma warning disable CA5350 // RFC 6238 and every authenticator app use HMAC-SHA1; the test must compute what a phone computes.
        byte[] hash = HMACSHA1.HashData(Base32(key), counter);
#pragma warning restore CA5350
        int offset = hash[^1] & 0x0F;
        int binary = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (binary % 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static byte[] Base32(string input)
    {
        const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        List<byte> bytes = [];
        int buffer = 0;
        int bits = 0;
        foreach (char c in input.Trim().TrimEnd('=').ToUpperInvariant())
        {
            buffer = (buffer << 5) | Alphabet.IndexOf(c, StringComparison.Ordinal);
            bits += 5;
            if (bits >= 8)
            {
                bytes.Add((byte)(buffer >> (bits - 8)));
                bits -= 8;
            }
        }

        return [.. bytes];
    }
}
