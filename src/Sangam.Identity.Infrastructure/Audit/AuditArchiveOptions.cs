using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Configuration;

namespace Sangam.Identity.Infrastructure.Audit;

/// <summary>
/// D-A, the founder's audit-retention decision: one year live in <c>audit_events</c>; then an encrypted archive kept
/// in India (the server's own disk, and the encrypted off-region backups, D-E) with IP addresses truncated and user
/// agents dropped; purged at seven years. Without <see cref="Directory"/> and <see cref="CertificatePath"/> nothing
/// is ever removed from the live table.
/// </summary>
public sealed class AuditArchiveOptions
{
    /// <summary>Configuration section.</summary>
    public const string SectionName = "Sangam:Audit";

    /// <summary>The shortest live period D-A allows.</summary>
    public const int MinimumLiveDays = 365;

    /// <summary>The archive's lifetime D-A sets.</summary>
    public const int MinimumPurgeYears = 7;

    /// <summary>The setting before D-A; refused at start-up, because it deleted without archiving.</summary>
    public const string LegacyRetentionKey = "Sangam:Audit:RetentionDays";

    /// <summary>Days an event stays live (never below <see cref="MinimumLiveDays"/>).</summary>
    public int LiveDays { get; set; } = MinimumLiveDays;

    /// <summary>Years an archived event is kept (never below <see cref="MinimumPurgeYears"/>).</summary>
    public int PurgeYears { get; set; } = MinimumPurgeYears;

    /// <summary>Where archive files are written (on the server, inside the backed-up area).</summary>
    public string? Directory { get; set; }

    /// <summary>The founder's archive certificate: public key only. Its private key stays offline with the founder.</summary>
    public string? CertificatePath { get; set; }

    /// <summary>Events per archive file.</summary>
    public int BatchSize { get; set; } = 20_000;

    /// <summary>Whether archiving is switched on (a directory and a certificate are set).</summary>
    public bool Enabled => !string.IsNullOrWhiteSpace(Directory) && !string.IsNullOrWhiteSpace(CertificatePath);

    /// <summary>The live period actually used.</summary>
    public TimeSpan Live => TimeSpan.FromDays(Math.Max(MinimumLiveDays, LiveDays));

    /// <summary>The archive lifetime actually used, in years.</summary>
    public int Years => Math.Max(MinimumPurgeYears, PurgeYears);

    /// <summary>Reads the settings.</summary>
    /// <param name="configuration">Configuration.</param>
    public static AuditArchiveOptions From(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        AuditArchiveOptions options = new();
        configuration.GetSection(SectionName).Bind(options);
        return options;
    }

    /// <summary>Loads the archive certificate (public key only is fine; a private key here is refused).</summary>
    public X509Certificate2 LoadCertificate()
    {
        X509Certificate2 certificate = X509CertificateLoader.LoadCertificateFromFile(CertificatePath ?? throw new InvalidOperationException("No audit-archive certificate is set."));
        if (certificate.HasPrivateKey)
        {
            certificate.Dispose();
            throw new InvalidOperationException("The audit-archive certificate must be the public certificate only; its private key stays offline with the founder.");
        }

        using RSA? rsa = certificate.GetRSAPublicKey();
        if (rsa is null || rsa.KeySize < 3072)
        {
            certificate.Dispose();
            throw new InvalidOperationException("The audit-archive certificate needs an RSA key of at least 3072 bits.");
        }

        return certificate;
    }

    /// <summary>Returns why the host must not start, or <see langword="null"/>.</summary>
    /// <param name="environmentName">The host environment name.</param>
    /// <param name="configuration">Configuration.</param>
    public static string? Validate(string environmentName, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(environmentName);
        ArgumentNullException.ThrowIfNull(configuration);
        if (!string.IsNullOrWhiteSpace(configuration[LegacyRetentionKey]))
        {
            return $"{LegacyRetentionKey} is no longer used: it deleted audit events without archiving them. D-A keeps them a year live, then in the encrypted archive (Sangam:Audit:Directory and Sangam:Audit:CertificatePath) for seven years. Remove the setting. Refusing to start.";
        }

        AuditArchiveOptions options = From(configuration);
        if (string.IsNullOrWhiteSpace(options.Directory) != string.IsNullOrWhiteSpace(options.CertificatePath))
        {
            return "The audit archive needs both Sangam:Audit:Directory and Sangam:Audit:CertificatePath, or neither. Refusing to start.";
        }

        if (options.Enabled)
        {
            try
            {
                using X509Certificate2 certificate = options.LoadCertificate();
            }
            catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                return "The audit-archive certificate (Sangam:Audit:CertificatePath) cannot be used: " + ex.Message + " Refusing to start.";
            }
        }

        return null;
    }
}
