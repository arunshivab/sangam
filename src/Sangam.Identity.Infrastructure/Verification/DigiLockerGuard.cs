using Microsoft.Extensions.Configuration;

namespace Sangam.Identity.Infrastructure.Verification;

/// <summary>
/// Start-up rule for DigiLocker (PR-26). Outside Development and Testing, when it is switched on, it needs its client id
/// and secret, https endpoints, and a real key of at least 32 characters for hashing DigiLocker ids.
/// </summary>
public static class DigiLockerGuard
{
    /// <summary>Returns why the host must not start, or <see langword="null"/>.</summary>
    /// <param name="environmentName">The host environment name.</param>
    /// <param name="configuration">Configuration.</param>
    public static string? Validate(string environmentName, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(environmentName);
        ArgumentNullException.ThrowIfNull(configuration);
        DigiLockerSettings settings = DigiLockerSettings.From(configuration);
        if (!settings.Enabled
            || string.Equals(environmentName, "Development", StringComparison.OrdinalIgnoreCase)
            || string.Equals(environmentName, "Testing", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string where = $"in the '{environmentName}' environment";
        if (string.IsNullOrWhiteSpace(settings.ClientId) || string.IsNullOrWhiteSpace(settings.ClientSecret))
        {
            return $"DigiLocker is enabled {where} without its client id and secret (Sangam:DigiLocker:ClientId, ClientSecret). Refusing to start.";
        }

        foreach (string url in new[] { settings.AuthorizeUrl, settings.TokenUrl, settings.UserUrl })
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || uri.Scheme != Uri.UriSchemeHttps)
            {
                return $"The DigiLocker endpoint '{url}' is not an https address {where}. Refusing to start.";
            }
        }

        if (settings.SubjectKey.Length < 32)
        {
            return $"DigiLocker is enabled {where} without a subject key of at least 32 characters (Sangam:DigiLocker:SubjectKey). Refusing to start: DigiLocker ids would be hashed with a public key.";
        }

        return null;
    }
}
