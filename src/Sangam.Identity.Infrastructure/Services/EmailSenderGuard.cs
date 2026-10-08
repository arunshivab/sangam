using Microsoft.Extensions.Configuration;
using Sangam.Identity.Infrastructure.Messaging;

namespace Sangam.Identity.Infrastructure.Services;

/// <summary>
/// Start-up rule for e-mail (OI-038, D-B): outside Development and Testing the development outbox must be off,
/// because it would expose one-time codes, and Anjal's API must be configured — its address over HTTPS (or a
/// private host name on the server's own network) and its API key — because codes cannot otherwise be delivered.
/// </summary>
public static class EmailSenderGuard
{
    /// <summary>Configuration key that switches on the development outbox.</summary>
    public const string OutboxKey = "Sangam:Email:UseOutbox";

    /// <summary>Configuration key of Anjal's API address (D-B).</summary>
    public const string AnjalBaseUrlKey = "Sangam:Anjal:BaseUrl";

    /// <summary>Configuration key of Anjal's API key (D-B).</summary>
    public const string AnjalApiKeyKey = "Sangam:Anjal:ApiKey";

    /// <summary>Configuration key: hand e-mail to Anjal in the background (default <see langword="true"/>).</summary>
    public const string DeliverInBackgroundKey = "Sangam:Email:DeliverInBackground";

    /// <summary>Returns why the host must not start, or <see langword="null"/> when e-mail is safe.</summary>
    /// <param name="environmentName">The host environment name.</param>
    /// <param name="configuration">Configuration.</param>
    public static string? Validate(string environmentName, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(environmentName);
        ArgumentNullException.ThrowIfNull(configuration);
        bool development = string.Equals(environmentName, "Development", StringComparison.OrdinalIgnoreCase)
            || string.Equals(environmentName, "Testing", StringComparison.OrdinalIgnoreCase);
        if (development)
        {
            return null;
        }

        string where = $"in the '{environmentName}' environment";
        if (configuration.GetValue<bool>(OutboxKey))
        {
            return $"The development e-mail outbox ({OutboxKey}) is enabled {where}. Refusing to start: it would expose one-time codes.";
        }

        AnjalOptions anjal = AnjalOptions.From(configuration);
        if (!anjal.Configured)
        {
            return $"Anjal's API is not configured ({AnjalBaseUrlKey}) {where}. One-time codes could not be delivered. Refusing to start.";
        }

        if (!Uri.TryCreate(anjal.BaseUrl, UriKind.Absolute, out Uri? address)
            || (address.Scheme != Uri.UriSchemeHttps && !(address.Scheme == Uri.UriSchemeHttp && IsPrivateName(address.Host))))
        {
            return $"Anjal's API address ({AnjalBaseUrlKey}) must be https, or http to a private host name on the server's own network (for example http://anjal:8080). Refusing to start.";
        }

        if (string.IsNullOrWhiteSpace(anjal.ApiKey) || anjal.ApiKey.Length < 20)
        {
            return $"Anjal's API key ({AnjalApiKeyKey}) is missing or shorter than 20 characters {where}. Refusing to start.";
        }

        return null;
    }

    /// <summary>A single-label name such as <c>anjal</c> (a container on the same Docker network) or localhost.</summary>
    private static bool IsPrivateName(string host)
        => string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) || !host.Contains('.', StringComparison.Ordinal);
}
