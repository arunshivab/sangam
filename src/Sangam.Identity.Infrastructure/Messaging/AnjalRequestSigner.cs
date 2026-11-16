using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Sangam.Identity.Infrastructure.Messaging;

/// <summary>
/// Signs Sangam's requests to Anjal as Anjal verifies them (rc.17, M1 (b); Anjal's signing spec of 13 November 2026):
/// ECDSA P-256 with SHA-256 over four lines joined by <c>\n</c> with no trailing newline — the method in upper case,
/// the raw target (path and query exactly as sent), the Unix-seconds timestamp exactly as sent, and the lowercase hex
/// SHA-256 of the exact body bytes. The signature is the 64-byte r‖s form (IEEE P1363), base64url without padding, and
/// travels in <see cref="SignatureHeader"/> beside <see cref="KeyIdHeader"/> and <see cref="TimestampHeader"/>. The API
/// key is still sent: the signature adds to it, never replaces it. The key id is worked out from the key itself
/// (<c>sk_</c> plus the first 12 hex characters of the SHA-256 of its SubjectPublicKeyInfo), so it cannot drift from
/// the key Anjal holds.
/// </summary>
public sealed class AnjalRequestSigner : IDisposable
{
    /// <summary>The header naming the signing key.</summary>
    public const string KeyIdHeader = "Anjal-Key-Id";

    /// <summary>The header carrying the Unix-seconds timestamp.</summary>
    public const string TimestampHeader = "Anjal-Timestamp";

    /// <summary>The header carrying the signature.</summary>
    public const string SignatureHeader = "Anjal-Signature";

    private readonly ECDsa _key;

    private AnjalRequestSigner(ECDsa key)
    {
        _key = key;
        KeyId = KeyIdOf(key);
    }

    /// <summary>The key id Anjal shows for this key's public half.</summary>
    public string KeyId { get; }

    /// <summary>
    /// Reads a P-256 private key from PEM (PKCS#8 <c>BEGIN PRIVATE KEY</c> or SEC1 <c>BEGIN EC PRIVATE KEY</c>).
    /// Anything else is refused, so a host with a wrong key does not start.
    /// </summary>
    /// <param name="pem">The private key in PEM.</param>
    /// <exception cref="InvalidOperationException">The text is not a P-256 private key.</exception>
    public static AnjalRequestSigner FromPem(string pem)
    {
        ArgumentNullException.ThrowIfNull(pem);
        ECDsa key = ECDsa.Create();
        try
        {
            key.ImportFromPem(pem);
            ECParameters parameters = key.ExportParameters(true);
            if (parameters.D is null || !parameters.Curve.IsNamed || !IsP256(parameters.Curve.Oid))
            {
                throw new InvalidOperationException("Sangam:Anjal:SigningKey must be a P-256 (prime256v1) private key; this one is not. Refusing to start.");
            }

            return new AnjalRequestSigner(key);
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException)
        {
            key.Dispose();
            throw new InvalidOperationException("Sangam:Anjal:SigningKey is not a readable P-256 private key in PEM. Refusing to start.", ex);
        }
        catch
        {
            key.Dispose();
            throw;
        }
    }

    /// <summary>The exact text that is signed.</summary>
    /// <param name="method">The HTTP method.</param>
    /// <param name="target">The raw target: path and query exactly as sent.</param>
    /// <param name="timestamp">The timestamp exactly as sent.</param>
    /// <param name="body">The exact body bytes (empty when there is no body).</param>
    public static string SigningString(string method, string target, string timestamp, ReadOnlySpan<byte> body)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(timestamp);
        string bodyHash = Convert.ToHexStringLower(SHA256.HashData(body));
        return method.ToUpperInvariant() + "\n" + target + "\n" + timestamp + "\n" + bodyHash;
    }

    /// <summary>Signs one request and adds the three headers to it.</summary>
    /// <param name="request">The request; its URI must be absolute.</param>
    /// <param name="body">The exact body bytes that will be sent (empty when there is none).</param>
    /// <param name="now">The time to stamp.</param>
    public void Sign(HttpRequestMessage request, ReadOnlySpan<byte> body, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(request);
        Uri uri = request.RequestUri is { IsAbsoluteUri: true } absolute
            ? absolute
            : throw new InvalidOperationException("A signed request needs an absolute URI.");
        string timestamp = now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        string text = SigningString(request.Method.Method, uri.PathAndQuery, timestamp, body);
        byte[] signature = _key.SignData(Encoding.UTF8.GetBytes(text), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        request.Headers.Remove(KeyIdHeader);
        request.Headers.Remove(TimestampHeader);
        request.Headers.Remove(SignatureHeader);
        request.Headers.TryAddWithoutValidation(KeyIdHeader, KeyId);
        request.Headers.TryAddWithoutValidation(TimestampHeader, timestamp);
        request.Headers.TryAddWithoutValidation(SignatureHeader, Base64Url(signature));
    }

    /// <summary>The key id Anjal gives a public key: <c>sk_</c> plus 12 hex characters of the SPKI's SHA-256.</summary>
    /// <param name="key">The key (its public half is used).</param>
    public static string KeyIdOf(ECDsa key)
    {
        ArgumentNullException.ThrowIfNull(key);
        byte[] spki = key.ExportSubjectPublicKeyInfo();
        return "sk_" + Convert.ToHexStringLower(SHA256.HashData(spki))[..12];
    }

    /// <inheritdoc />
    public void Dispose() => _key.Dispose();

    private static bool IsP256(Oid oid)
        => string.Equals(oid.Value, "1.2.840.10045.3.1.7", StringComparison.Ordinal)
            || string.Equals(oid.FriendlyName, "nistP256", StringComparison.OrdinalIgnoreCase)
            || string.Equals(oid.FriendlyName, "ECDSA_P256", StringComparison.OrdinalIgnoreCase)
            || string.Equals(oid.FriendlyName, "prime256v1", StringComparison.OrdinalIgnoreCase);

    private static string Base64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
