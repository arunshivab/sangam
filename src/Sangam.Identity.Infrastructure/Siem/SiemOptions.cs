using Microsoft.Extensions.Configuration;

namespace Sangam.Identity.Infrastructure.Siem;

/// <summary>
/// PR-32 (CAP-084, SGM-801): streaming Sangam's audit events to a security information and event management system.
/// Off by default. Section <c>Sangam:Siem</c>.
/// </summary>
public sealed class SiemOptions
{
    /// <summary>Configuration section.</summary>
    public const string SectionName = "Sangam:Siem";

    /// <summary>Whether events are streamed. Default off.</summary>
    public bool Enabled { get; set; }

    /// <summary>The receiver's host name or address.</summary>
    public string Host { get; set; } = string.Empty;

    /// <summary>The receiver's TCP port. Default 6514, the syslog-over-TLS port (RFC 5425).</summary>
    public int Port { get; set; } = 6514;

    /// <summary>
    /// <c>cef</c> (default): ArcSight Common Event Format in an RFC 5424 syslog message, framed by octet counting
    /// (RFC 5425). <c>json</c>: one JSON object per line, each carrying the event in the shared audit schema (SGM-208).
    /// </summary>
    public string Format { get; set; } = "cef";

    /// <summary>Whether the connection uses TLS. Default on; plain TCP is refused outside Development and Testing.</summary>
    public bool Tls { get; set; } = true;

    /// <summary>The name the receiver's certificate must carry. Default <see cref="Host"/>.</summary>
    public string? ServerName { get; set; }

    /// <summary>A PEM or DER file with the certificate authority that signed the receiver's certificate, when it is
    /// not publicly trusted. Only that authority is then accepted.</summary>
    public string? CaCertificatePath { get; set; }

    /// <summary>A PKCS#12 file with Sangam's client certificate, when the receiver asks for one (mutual TLS).</summary>
    public string? ClientCertificatePath { get; set; }

    /// <summary>The password of <see cref="ClientCertificatePath"/>.</summary>
    public string? ClientCertificatePassword { get; set; }

    /// <summary>Events sent per round trip. Default 200.</summary>
    public int BatchSize { get; set; } = 200;

    /// <summary>How often to look for new events. Default every 10 seconds.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Where a first start begins: <c>now</c> (default) sends only events recorded from then on; <c>beginning</c>
    /// sends every event still in the live audit log.
    /// </summary>
    public string StartFrom { get; set; } = "now";

    /// <summary>Reads the settings.</summary>
    /// <param name="configuration">Configuration.</param>
    public static SiemOptions From(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        SiemOptions options = new();
        configuration.GetSection(SectionName).Bind(options);
        return options;
    }

    /// <summary>Returns why the host must not start with these settings, or <see langword="null"/>.</summary>
    /// <param name="environmentName">The host environment name.</param>
    /// <param name="configuration">Configuration.</param>
    public static string? Validate(string environmentName, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(environmentName);
        SiemOptions options = From(configuration);
        if (!options.Enabled)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(options.Host) || options.Port is < 1 or > 65535)
        {
            return "SIEM streaming is enabled without a receiver (Sangam:Siem:Host and Port). Refusing to start."; // i18n-ignore: start-up refusal, for the operator log
        }

        if (options.Format is not ("cef" or "json"))
        {
            return $"Sangam:Siem:Format must be 'cef' or 'json', not '{options.Format}'. Refusing to start."; // i18n-ignore: start-up refusal, for the operator log
        }

        if (options.StartFrom is not ("now" or "beginning"))
        {
            return $"Sangam:Siem:StartFrom must be 'now' or 'beginning', not '{options.StartFrom}'. Refusing to start."; // i18n-ignore: start-up refusal, for the operator log
        }

        bool development = environmentName is "Development" or "Testing";
        if (!options.Tls && !development)
        {
            return $"SIEM streaming over plain TCP is not allowed in the '{environmentName}' environment: audit events carry "
                + "addresses and account ids (Sangam:Siem:Tls). Refusing to start."; // i18n-ignore: start-up refusal, for the operator log
        }

        foreach (string? path in new[] { options.CaCertificatePath, options.ClientCertificatePath })
        {
            if (!string.IsNullOrWhiteSpace(path) && !File.Exists(path))
            {
                return $"The SIEM certificate file '{path}' does not exist. Refusing to start."; // i18n-ignore: start-up refusal, for the operator log
            }
        }

        return null;
    }
}
