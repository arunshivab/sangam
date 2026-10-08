using System.Diagnostics.Metrics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Configuration;

namespace Sangam.Identity.Infrastructure.Monitoring;

/// <summary>
/// Gauges about the host itself (D-H): that it is up, its free disk space, and how long its certificates have left —
/// the token signing and encryption certificates and the key-ring certificate, wherever this host has them.
/// </summary>
public sealed class HostProbe
{
    private static readonly TimeSpan CertificateRecheck = TimeSpan.FromHours(1);
    private readonly IConfiguration _configuration;
    private readonly IReadOnlyList<string> _paths;
    private readonly Lock _gate = new();
    private List<Measurement<double>> _certificates = [];
    private DateTimeOffset _certificatesReadAt = DateTimeOffset.MinValue;

    /// <summary>Initialises the probe and registers its gauges.</summary>
    /// <param name="configuration">Configuration (<c>Sangam:Monitoring:DiskPaths</c>, certificate paths and passwords).</param>
    public HostProbe(IConfiguration configuration)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _paths = [.. (configuration["Sangam:Monitoring:DiskPaths"] ?? "/").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
        SangamMetrics.Instance.CreateObservableGauge(SangamMetrics.HostUp, () => 1);
        SangamMetrics.Instance.CreateObservableGauge(SangamMetrics.DiskFreePercent, DiskFree);
        SangamMetrics.Instance.CreateObservableGauge(SangamMetrics.CertificateDaysLeft, CertificateDays);
    }

    private IEnumerable<Measurement<double>> DiskFree()
    {
        foreach (string path in _paths)
        {
            DriveInfo drive;
            try
            {
                drive = new DriveInfo(path);
                if (!drive.IsReady || drive.TotalSize <= 0)
                {
                    continue;
                }
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
            {
                continue;
            }

            // As df counts it: what the process may still use, out of what it could use (blocks reserved for root left out).
            long usable = drive.TotalSize - drive.TotalFreeSpace + drive.AvailableFreeSpace;
            yield return new Measurement<double>(100.0 * drive.AvailableFreeSpace / usable, new KeyValuePair<string, object?>("path", path));
        }
    }

    private List<Measurement<double>> CertificateDays()
    {
        lock (_gate)
        {
            if (DateTimeOffset.UtcNow - _certificatesReadAt < CertificateRecheck)
            {
                return _certificates;
            }

            _certificatesReadAt = DateTimeOffset.UtcNow;
            List<Measurement<double>> found = [];
            foreach ((string name, string prefix) in new[]
            {
                ("signing", "Sangam:Certificates:Signing:0"),
                ("encryption", "Sangam:Certificates:Encryption:0"),
                ("key_ring", "Sangam:DataProtection:Certificates:0"),
            })
            {
                string? path = _configuration[prefix + ":Path"];
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                {
                    continue;
                }

                try
                {
                    using X509Certificate2 certificate = X509CertificateLoader.LoadPkcs12FromFile(path, _configuration[prefix + ":Password"]);
                    found.Add(new Measurement<double>((certificate.NotAfter.ToUniversalTime() - DateTime.UtcNow).TotalDays, new KeyValuePair<string, object?>("cert", name)));
                }
                catch (CryptographicException)
                {
                    found.Add(new Measurement<double>(-1, new KeyValuePair<string, object?>("cert", name)));
                }
            }

            _certificates = found;
            return found;
        }
    }
}
