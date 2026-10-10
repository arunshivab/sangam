using Microsoft.Extensions.Configuration;

namespace Sangam.Identity.Infrastructure.Customisation;

/// <summary>
/// Start-up rule for the virus scanner (rc.5, ASVS V12.4.2): outside Development and Testing, the consoles where logos
/// are uploaded refuse to start without a scanner, because no logo may be kept unscanned.
/// </summary>
public static class AntivirusGuard
{
    /// <summary>Returns why the host must not start, or <see langword="null"/> when a scanner is configured.</summary>
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

        return string.IsNullOrWhiteSpace(configuration[ClamAvScanner.HostKey])
            ? $"No virus scanner is configured ({ClamAvScanner.HostKey}: the server's ClamAV, shared with Anjal) in the '{environmentName}' environment. Uploaded logos could not be scanned. Refusing to start."
            : null;
    }
}
