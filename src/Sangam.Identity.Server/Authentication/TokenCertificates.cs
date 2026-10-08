using System.Security.Cryptography.X509Certificates;

namespace Sangam.Identity.Server.Authentication;

/// <summary>
/// Loads the production token certificates (PR-10, SGM-803). Configuration lists each kind as an
/// array, current first, then any previous ones kept published during a rotation:
/// <c>Sangam:Certificates:Signing:0:Path</c> and <c>:Password</c>, likewise
/// <c>Sangam:Certificates:Encryption:n</c>. Passwords come from secret files, never settings files.
/// </summary>
public static class TokenCertificates
{
    /// <summary>Configuration section.</summary>
    public const string SectionName = "Sangam:Certificates";

    /// <summary>Loads the signing and encryption certificates, or explains what is wrong.</summary>
    /// <param name="configuration">Configuration.</param>
    /// <returns>The certificates, each list current first.</returns>
    /// <exception cref="InvalidOperationException">When a kind is missing, a file is unreadable, or a certificate has no private key.</exception>
    public static (IReadOnlyList<X509Certificate2> Signing, IReadOnlyList<X509Certificate2> Encryption) Load(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return (LoadKind(configuration, "Signing"), LoadKind(configuration, "Encryption"));
    }

    private static List<X509Certificate2> LoadKind(IConfiguration configuration, string kind)
    {
        List<X509Certificate2> certificates = [];
        foreach (IConfigurationSection entry in configuration.GetSection($"{SectionName}:{kind}").GetChildren())
        {
            string? path = entry["Path"];
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new InvalidOperationException($"{SectionName}:{kind}:{entry.Key}:Path is empty.");
            }

            if (!File.Exists(path))
            {
                throw new InvalidOperationException($"The {kind.ToLowerInvariant()} certificate file {path} ({SectionName}:{kind}:{entry.Key}) does not exist.");
            }

            X509Certificate2 certificate = X509CertificateLoader.LoadPkcs12FromFile(path, entry["Password"], X509KeyStorageFlags.EphemeralKeySet);
            if (!certificate.HasPrivateKey)
            {
                certificate.Dispose();
                throw new InvalidOperationException($"The {kind.ToLowerInvariant()} certificate {path} has no private key.");
            }

            certificates.Add(certificate);
        }

        if (certificates.Count == 0)
        {
            throw new InvalidOperationException(
                $"No production {kind.ToLowerInvariant()} certificate is configured ({SectionName}:{kind}:0:Path). See docs/go-live-checklist.md. Refusing to start with development keys.");
        }

        return certificates;
    }
}
