using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Sangam.Admin.Web.Components.Common;

/// <summary>One token-signing key the identity server publishes (rc.11).</summary>
/// <param name="KeyId">The key's id (<c>kid</c>), as tokens name it.</param>
/// <param name="Algorithm">The algorithm, when published.</param>
/// <param name="Subject">The certificate's subject.</param>
/// <param name="ValidFrom">When the certificate became valid.</param>
/// <param name="ValidUntil">When it stops being valid.</param>
/// <param name="Fingerprint">The certificate's SHA-256 fingerprint, in pairs.</param>
/// <param name="InUse">Whether it is the key new tokens are signed with.</param>
public sealed record SigningKeyRow(string KeyId, string? Algorithm, string Subject, DateTimeOffset ValidFrom, DateTimeOffset ValidUntil, string Fingerprint, bool InUse);

/// <summary>What the identity server publishes, or why it could not be read.</summary>
/// <param name="Keys">The signing keys, in the order published.</param>
/// <param name="Problem">Why they could not be read, if they could not.</param>
public sealed record SigningKeysResult(IReadOnlyList<SigningKeyRow> Keys, string? Problem);

/// <summary>Where the operator console reads the signing keys from.</summary>
public interface ISigningKeySource
{
    /// <summary>Reads the keys.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<SigningKeysResult> ReadAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// rc.11 (founder decision, 13 Oct 2026): the signing keys page is read-only. It shows what the identity server publishes in
/// its key set (JWKS), read through the console's own OpenID Connect configuration: the same public keys every application
/// already fetches, so nothing secret reaches this host. OpenIddict signs with the first key it lists; the others stay
/// published so tokens signed before a rotation still verify.
/// </summary>
public sealed class OidcSigningKeySource : ISigningKeySource
{
    private readonly IOptionsMonitor<OpenIdConnectOptions> _options;

    /// <summary>Initialises the source.</summary>
    /// <param name="options">The console's OpenID Connect options.</param>
    public OidcSigningKeySource(IOptionsMonitor<OpenIdConnectOptions> options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <inheritdoc />
    public async Task<SigningKeysResult> ReadAsync(CancellationToken cancellationToken = default)
    {
        OpenIdConnectOptions options = _options.Get(OpenIdConnectDefaults.AuthenticationScheme);
        if (options.ConfigurationManager is null)
        {
            return new SigningKeysResult([], "The identity server's address is not configured."); // i18n-key
        }

        try
        {
            OpenIdConnectConfiguration configuration = await options.ConfigurationManager.GetConfigurationAsync(cancellationToken).ConfigureAwait(false);
            return new SigningKeysResult(FromKeySet(configuration.JsonWebKeySet?.Keys ?? []), null);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or HttpRequestException or TaskCanceledException)
        {
            return new SigningKeysResult([], "The identity server's published keys could not be read just now."); // i18n-key
        }
    }

    /// <summary>The signing keys in a published key set, with the certificates they carry.</summary>
    /// <param name="keys">The key set's keys, in published order.</param>
    public static IReadOnlyList<SigningKeyRow> FromKeySet(IEnumerable<JsonWebKey> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        List<SigningKeyRow> rows = [];
        foreach (JsonWebKey key in keys)
        {
            if (key.Use is not null && key.Use != JsonWebKeyUseNames.Sig)
            {
                continue;
            }

            if (key.X5c.Count == 0)
            {
                continue;
            }

            using X509Certificate2 certificate = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(key.X5c[0]));
            rows.Add(new SigningKeyRow(
                key.Kid ?? string.Empty,
                key.Alg,
                certificate.Subject,
                new DateTimeOffset(certificate.NotBefore.ToUniversalTime()),
                new DateTimeOffset(certificate.NotAfter.ToUniversalTime()),
                Pairs(certificate.GetCertHashString(HashAlgorithmName.SHA256)),
                rows.Count == 0));
        }

        return rows;
    }

    private static string Pairs(string hex) => string.Join(':', Enumerable.Range(0, hex.Length / 2).Select(i => hex.Substring(i * 2, 2)));
}
