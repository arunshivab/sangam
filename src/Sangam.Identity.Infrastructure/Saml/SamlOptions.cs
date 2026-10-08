using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Extensions.Configuration;

namespace Sangam.Identity.Infrastructure.Saml;

/// <summary>
/// The SAML identity provider's settings (PR-22): <c>Sangam:Saml:Certificates:0:Path</c> and <c>:Password</c> (a
/// SAML-only signing key, SGM-803; previous ones stay published for a rotation), <c>Sangam:Saml:PairwiseKey</c> (the
/// secret that derives each service provider's own NameID for a person — never change it), and optionally
/// <c>Sangam:Saml:EntityId</c>. Without a certificate SAML is off outside Development and Testing, where a throwaway
/// key is made at start.
/// </summary>
public sealed class SamlOptions
{
    private const string DevelopmentPairwiseKey = "sangam-development-saml-pairwise-key-change-me";

    /// <summary>The signing certificates, current first.</summary>
    public IReadOnlyList<X509Certificate2> Certificates { get; private init; } = [];

    /// <summary>Whether the identity provider answers at all.</summary>
    public bool Enabled => Certificates.Count > 0;

    /// <summary>The configured entity id, or <see langword="null"/> to use <c>{issuer}/saml</c>.</summary>
    public string? EntityId { get; private init; }

    private byte[] PairwiseKey { get; init; } = [];

    /// <summary>Reads the settings; in Development and Testing makes a throwaway signing key when none is set.</summary>
    /// <param name="configuration">Configuration.</param>
    /// <param name="development">Whether this is Development or Testing.</param>
    public static SamlOptions From(IConfiguration configuration, bool development)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        List<X509Certificate2> certificates = [];
        foreach (IConfigurationSection section in configuration.GetSection("Sangam:Saml:Certificates").GetChildren())
        {
            if (section["Path"] is { Length: > 0 } path)
            {
                certificates.Add(X509CertificateLoader.LoadPkcs12FromFile(path, section["Password"], X509KeyStorageFlags.EphemeralKeySet));
            }
        }

        if (certificates.Count == 0 && development)
        {
            using RSA rsa = RSA.Create(2048);
            CertificateRequest request = new("CN=Sangam SAML (development)", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using X509Certificate2 created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
            certificates.Add(X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pkcs12), null, X509KeyStorageFlags.EphemeralKeySet));
        }

        string key = configuration["Sangam:Saml:PairwiseKey"] is { Length: > 0 } configured ? configured : development ? DevelopmentPairwiseKey : string.Empty;
        return new SamlOptions
        {
            Certificates = certificates,
            EntityId = configuration["Sangam:Saml:EntityId"] is { Length: > 0 } entity ? entity : null,
            PairwiseKey = Encoding.UTF8.GetBytes(key),
        };
    }

    /// <summary>Returns why the host must not start, or <see langword="null"/>.</summary>
    /// <param name="environmentName">Environment name.</param>
    /// <param name="configuration">Configuration.</param>
    public static string? Validate(string environmentName, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(environmentName);
        ArgumentNullException.ThrowIfNull(configuration);
        bool development = environmentName is "Development" or "Testing";
        if (development || !configuration.GetSection("Sangam:Saml:Certificates").GetChildren().Any())
        {
            return null;
        }

        string key = configuration["Sangam:Saml:PairwiseKey"] ?? string.Empty;
        if (key.Length < 32 || key.Contains("change-me", StringComparison.OrdinalIgnoreCase))
        {
            return "SAML is set up (Sangam:Saml:Certificates) but Sangam:Saml:PairwiseKey is missing, shorter than 32 characters, or a development value. Refusing to start."; // i18n-ignore: a start-up log line for the operator
        }

        try
        {
            SamlOptions options = From(configuration, development: false);
            return options.Certificates.All(c => c.HasPrivateKey && c.GetRSAPrivateKey() is not null) ? null : "The SAML signing certificate needs its RSA private key. Refusing to start."; // i18n-ignore: a start-up log line
        }
        catch (CryptographicException ex)
        {
            return "The SAML signing certificate (Sangam:Saml:Certificates) cannot be read: " + ex.Message + " Refusing to start.";
        }
    }

    /// <summary>The entity id: configured, or the issuer's <c>/saml</c>.</summary>
    /// <param name="issuer">The issuer (scheme, host, base path).</param>
    public string EntityIdFor(string issuer)
    {
        ArgumentNullException.ThrowIfNull(issuer);
        return EntityId ?? issuer.TrimEnd('/') + "/saml";
    }

    /// <summary>
    /// The person's NameID at one service provider: an HMAC of the person and the SP's entity id, so two SPs can never
    /// match their users with each other, and the same person always gets the same value at the same SP.
    /// </summary>
    /// <param name="userId">The person.</param>
    /// <param name="spEntityId">The service provider.</param>
    public string PairwiseNameId(Guid userId, string spEntityId)
    {
        ArgumentNullException.ThrowIfNull(spEntityId);
        byte[] hash = HMACSHA256.HashData(PairwiseKey, Encoding.UTF8.GetBytes(userId.ToString("D") + "\n" + spEntityId));
        return Convert.ToBase64String(hash).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
