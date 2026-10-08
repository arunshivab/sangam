using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Sangam.Identity.Infrastructure.Provisioning;

/// <summary>
/// Webhook signatures in the Standard Webhooks form (SGM-217 §4): headers <c>webhook-id</c>, <c>webhook-timestamp</c>
/// (Unix seconds) and <c>webhook-signature</c> = <c>v1,</c> + base64 HMAC-SHA256 over <c>id.timestamp.body</c>, keyed
/// with the bytes of the endpoint's secret (<c>whsec_</c> + base64). During a rotation both secrets sign, space-separated.
/// Receivers reject a timestamp more than five minutes from their clock and an id they have seen.
/// </summary>
public static class WebhookSigner
{
    /// <summary>The secret prefix.</summary>
    public const string SecretPrefix = "whsec_";

    /// <summary>How far a receiver lets the timestamp stray.</summary>
    public static readonly TimeSpan Tolerance = TimeSpan.FromMinutes(5);

    /// <summary>A new secret: 32 random bytes.</summary>
    public static string NewSecret() => SecretPrefix + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    /// <summary>The <c>webhook-signature</c> header value for these secrets.</summary>
    /// <param name="id">The message id.</param>
    /// <param name="timestamp">Unix seconds.</param>
    /// <param name="body">The body, exactly as sent.</param>
    /// <param name="secrets">The signing secrets (current first).</param>
    public static string Sign(string id, long timestamp, string body, params string[] secrets)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(secrets);
        byte[] content = Encoding.UTF8.GetBytes(id + "." + timestamp.ToString(CultureInfo.InvariantCulture) + "." + body);
        return string.Join(' ', secrets.Select(secret => "v1," + Convert.ToBase64String(HMACSHA256.HashData(Key(secret), content))));
    }

    /// <summary>Checks a received message as a receiver should (the published reference, also used by the tests).</summary>
    /// <param name="id">The <c>webhook-id</c> header.</param>
    /// <param name="timestamp">The <c>webhook-timestamp</c> header.</param>
    /// <param name="signature">The <c>webhook-signature</c> header.</param>
    /// <param name="body">The body, exactly as received.</param>
    /// <param name="secret">The receiver's secret.</param>
    /// <param name="now">The receiver's clock.</param>
    public static bool Verify(string id, string timestamp, string signature, string body, string secret, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(signature);
        if (!long.TryParse(timestamp, NumberStyles.None, CultureInfo.InvariantCulture, out long seconds)
            || (now - DateTimeOffset.FromUnixTimeSeconds(seconds)).Duration() > Tolerance)
        {
            return false;
        }

        byte[] expected = Encoding.ASCII.GetBytes(Sign(id, seconds, body, secret));
        return signature.Split(' ', StringSplitOptions.RemoveEmptyEntries).Any(part => CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(part), expected));
    }

    private static byte[] Key(string secret)
    {
        ArgumentNullException.ThrowIfNull(secret);
        return Convert.FromBase64String(secret.StartsWith(SecretPrefix, StringComparison.Ordinal) ? secret[SecretPrefix.Length..] : secret);
    }
}
