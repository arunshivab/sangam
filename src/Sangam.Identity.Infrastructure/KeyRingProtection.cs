using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;

namespace Sangam.Identity.Infrastructure;

/// <summary>
/// Encrypts the data-protection key ring at rest with a certificate (V-01). Configuration lists the
/// certificates current first: <c>Sangam:DataProtection:Certificates:0:Path</c> and <c>:Password</c>.
/// New keys are encrypted with the first; every listed certificate can decrypt, so after a rotation
/// the previous certificate stays listed until the keys it protected have expired (SGM-803).
/// </summary>
public static class KeyRingProtection
{
    /// <summary>Configuration section.</summary>
    public const string SectionName = "Sangam:DataProtection:Certificates";

    /// <summary>Applies certificate protection when certificates are configured.</summary>
    /// <param name="builder">The data-protection builder.</param>
    /// <param name="configuration">Configuration.</param>
    /// <returns>Whether certificates were configured.</returns>
    public static bool Apply(IDataProtectionBuilder builder, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configuration);
        List<X509Certificate2> certificates = Load(configuration);
        if (certificates.Count == 0)
        {
            return false;
        }

        builder.ProtectKeysWithCertificate(certificates[0]);
        builder.UnprotectKeysWithAnyCertificate([.. certificates]);
        return true;
    }

    /// <summary>
    /// Returns why a host must not start, or <see langword="null"/>. Outside Development and Testing
    /// the key ring must be persisted (or sessions break on every restart) and encrypted.
    /// </summary>
    /// <param name="environmentName">The host environment name.</param>
    /// <param name="configuration">Configuration.</param>
    public static string? Validate(string environmentName, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(environmentName);
        ArgumentNullException.ThrowIfNull(configuration);
        if (string.Equals(environmentName, "Development", StringComparison.OrdinalIgnoreCase)
            || string.Equals(environmentName, "Testing", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (!configuration.GetValue(DependencyInjection.PersistKeysKey, true))
        {
            return $"The data-protection key ring is not persisted ({DependencyInjection.PersistKeysKey} = false) in the '{environmentName}' environment. Sign-ins would break on every restart. Refusing to start.";
        }

        if (!configuration.GetSection(SectionName).GetChildren().Any())
        {
            return $"No key-ring certificate is configured ({SectionName}:0:Path) in the '{environmentName}' environment. Data-protection keys would be stored unencrypted. Refusing to start.";
        }

        return null;
    }

    private static List<X509Certificate2> Load(IConfiguration configuration)
    {
        List<X509Certificate2> certificates = [];
        foreach (IConfigurationSection entry in configuration.GetSection(SectionName).GetChildren())
        {
            string? path = entry["Path"];
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                throw new InvalidOperationException($"The key-ring certificate file '{path}' ({SectionName}:{entry.Key}:Path) does not exist.");
            }

            X509Certificate2 certificate = X509CertificateLoader.LoadPkcs12FromFile(path, entry["Password"], X509KeyStorageFlags.EphemeralKeySet);
            if (!certificate.HasPrivateKey)
            {
                certificate.Dispose();
                throw new InvalidOperationException($"The key-ring certificate {path} has no private key.");
            }

            certificates.Add(certificate);
        }

        return certificates;
    }
}
