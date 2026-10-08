using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Sangam.Client;

/// <summary>
/// Verifies Sangam's signed webhooks (Standard Webhooks, SGM-217): the <c>webhook-signature</c> header holds one or more
/// space-separated <c>v1,</c> signatures, each the base64 HMAC-SHA256 of <c>id.timestamp.body</c> under the bytes of the
/// <c>whsec_</c> secret. A message more than five minutes from the clock is refused. Check every message, on the body
/// exactly as received.
/// </summary>
public static class SangamWebhook
{
    /// <summary>The secret prefix.</summary>
    public const string SecretPrefix = "whsec_";

    /// <summary>How far the timestamp may be from the clock.</summary>
    public static TimeSpan Tolerance { get; } = TimeSpan.FromMinutes(5);

    /// <summary>Whether a message is Sangam's and recent.</summary>
    /// <param name="id">The <c>webhook-id</c> header.</param>
    /// <param name="timestamp">The <c>webhook-timestamp</c> header.</param>
    /// <param name="signatures">The <c>webhook-signature</c> header.</param>
    /// <param name="body">The body, exactly as received.</param>
    /// <param name="secret">The endpoint's secret, <c>whsec_…</c>.</param>
    /// <param name="now">The time to check against; the clock when omitted.</param>
    public static bool Verify(string? id, string? timestamp, string? signatures, string body, string secret, DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(secret);
        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(signatures)
            || !long.TryParse(timestamp, NumberStyles.None, CultureInfo.InvariantCulture, out long seconds)
            || Math.Abs(((now ?? DateTimeOffset.UtcNow) - DateTimeOffset.FromUnixTimeSeconds(seconds)).TotalSeconds) > Tolerance.TotalSeconds
            || !secret.StartsWith(SecretPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        byte[] key;
        try
        {
            key = Convert.FromBase64String(secret[SecretPrefix.Length..]);
        }
        catch (FormatException)
        {
            return false;
        }

        byte[] expected = Encoding.ASCII.GetBytes("v1," + Convert.ToBase64String(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes($"{id}.{timestamp}.{body}"))));
        return signatures.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Any(s => CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(s), expected));
    }
}
