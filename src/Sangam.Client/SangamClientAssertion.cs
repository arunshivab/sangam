using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Sangam.Client;

/// <summary>
/// rc.15: signs this application in to Sangam with a private key instead of a client secret (<c>private_key_jwt</c>,
/// RFC 7523). Each call to <see cref="Create"/> makes a new assertion: <c>iss</c> and <c>sub</c> are the client id,
/// <c>aud</c> is Sangam's issuer, a fresh <c>jti</c>, two minutes to live, typed <c>client-authentication+jwt</c> and
/// signed with the key, whose id (<c>kid</c>) is its RFC 7638 thumbprint — the key id the partner console shows. Sangam
/// accepts each assertion once.
/// </summary>
public sealed class SangamClientAssertion : IDisposable
{
    /// <summary>The <c>client_assertion_type</c> sent with each assertion.</summary>
    public const string AssertionType = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer";

    /// <summary>The JOSE type of the assertion.</summary>
    public const string TokenType = "client-authentication+jwt";

    /// <summary>How long each assertion lives.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(2);

    private static readonly JsonWebTokenHandler Handler = new();

    private readonly AsymmetricAlgorithm _key;
    private readonly SigningCredentials _credentials;
    private readonly string _clientId;
    private readonly string _audience;

    private SangamClientAssertion(AsymmetricAlgorithm key, SigningCredentials credentials, string keyId, string clientId, string audience)
    {
        _key = key;
        _credentials = credentials;
        KeyId = keyId;
        _clientId = clientId;

        // Sangam's issuer, written as discovery gives it (with its trailing slash).
        _audience = audience.TrimEnd('/') + "/";
    }

    /// <summary>The key's id, as the partner console shows it.</summary>
    public string KeyId { get; }

    /// <summary>The assertion maker for <paramref name="options"/>, or <see langword="null"/> when it signs in with a secret.</summary>
    /// <param name="options">Sangam settings.</param>
    /// <exception cref="InvalidOperationException">The private key cannot be read.</exception>
    public static SangamClientAssertion? From(SangamOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        string? pem = options.PrivateKeyPem;
        if (string.IsNullOrWhiteSpace(pem) && !string.IsNullOrWhiteSpace(options.PrivateKeyPath))
        {
            pem = File.ReadAllText(options.PrivateKeyPath);
        }

        return string.IsNullOrWhiteSpace(pem) ? null : FromPem(pem, options.ClientId, options.Authority);
    }

    /// <summary>The assertion maker for a PEM private key.</summary>
    /// <param name="privateKeyPem">An RSA (2048 bits or more) or elliptic-curve (P-256, P-384, P-521) private key, in PEM.</param>
    /// <param name="clientId">This application's client id.</param>
    /// <param name="authority">Sangam's address (its issuer).</param>
    /// <exception cref="InvalidOperationException">The private key cannot be read.</exception>
    public static SangamClientAssertion FromPem(string privateKeyPem, string clientId, string authority)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(privateKeyPem);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentException.ThrowIfNullOrWhiteSpace(authority);
        if (!privateKeyPem.Contains("PRIVATE KEY", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Sangam: PrivateKeyPem must be a PEM private key (-----BEGIN PRIVATE KEY-----). The public key goes on the partner console.");
        }

#pragma warning disable CA2000 // The key passes to the returned assertion maker; FromRsa and FromEc dispose it if they refuse it.
        RSA? rsa = TryRsa(privateKeyPem);
        if (rsa is not null)
        {
            return FromRsa(rsa, clientId, authority);
        }

        ECDsa ec = TryEc(privateKeyPem) ?? throw new InvalidOperationException("Sangam: PrivateKeyPem is not an RSA or elliptic-curve private key Sangam can read.");
        return FromEc(ec, clientId, authority);
#pragma warning restore CA2000
    }

    private static SangamClientAssertion FromRsa(RSA rsa, string clientId, string authority)
    {
        try
        {
            if (rsa.KeySize < 2048)
            {
                throw new InvalidOperationException("Sangam: the RSA key must have at least 2048 bits.");
            }

            RSAParameters p = rsa.ExportParameters(false);
            string kid = Thumbprint($"{{\"e\":\"{Base64Url(p.Exponent!)}\",\"kty\":\"RSA\",\"n\":\"{Base64Url(p.Modulus!)}\"}}");
            SangamClientAssertion made = new(rsa, new SigningCredentials(new RsaSecurityKey(rsa) { KeyId = kid }, SecurityAlgorithms.RsaSha256), kid, clientId, authority);
            rsa = null!;
            return made;
        }
        finally
        {
            rsa?.Dispose();
        }
    }

    private static SangamClientAssertion FromEc(ECDsa ec, string clientId, string authority)
    {
        try
        {
            ECParameters q = ec.ExportParameters(false);
            (string crv, string alg) = q.Curve.Oid?.Value switch
            {
                "1.2.840.10045.3.1.7" => ("P-256", SecurityAlgorithms.EcdsaSha256),
                "1.3.132.0.34" => ("P-384", SecurityAlgorithms.EcdsaSha384),
                "1.3.132.0.35" => ("P-521", SecurityAlgorithms.EcdsaSha512),
                _ => throw new InvalidOperationException("Sangam: use an elliptic-curve key on P-256, P-384 or P-521."),
            };
            string kid = Thumbprint($"{{\"crv\":\"{crv}\",\"kty\":\"EC\",\"x\":\"{Base64Url(q.Q.X!)}\",\"y\":\"{Base64Url(q.Q.Y!)}\"}}");
            SangamClientAssertion made = new(ec, new SigningCredentials(new ECDsaSecurityKey(ec) { KeyId = kid }, alg), kid, clientId, authority);
            ec = null!;
            return made;
        }
        finally
        {
            ec?.Dispose();
        }
    }

    /// <summary>A new assertion, for one request to Sangam's token endpoint.</summary>
    public string Create()
    {
        DateTime now = DateTime.UtcNow;
        return Handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = _clientId,
            Audience = _audience,
            Claims = new Dictionary<string, object>
            {
                [JwtRegisteredClaimNames.Sub] = _clientId,
                [JwtRegisteredClaimNames.Jti] = Base64Url(RandomNumberGenerator.GetBytes(16)),
            },
            IssuedAt = now,
            NotBefore = now,
            Expires = now + Lifetime,
            TokenType = TokenType,
            SigningCredentials = _credentials,
        });
    }

    /// <inheritdoc />
    public void Dispose() => _key.Dispose();

    private static RSA? TryRsa(string pem)
    {
        RSA rsa = RSA.Create();
        try
        {
            rsa.ImportFromPem(pem);
            return rsa;
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException)
        {
            rsa.Dispose();
            return null;
        }
    }

    private static ECDsa? TryEc(string pem)
    {
        ECDsa ec = ECDsa.Create();
        try
        {
            ec.ImportFromPem(pem);
            return ec;
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException)
        {
            ec.Dispose();
            return null;
        }
    }

    private static string Thumbprint(string canonical) => Base64Url(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
