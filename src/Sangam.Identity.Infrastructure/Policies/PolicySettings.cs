using Microsoft.Extensions.Configuration;
using Sangam.Identity.Application.Security;
using Sangam.Identity.Domain;
using Sangam.Identity.Domain.Enums;

namespace Sangam.Identity.Infrastructure.Policies;

/// <summary>
/// Platform policy and breached-password settings (PR-16; rc.5). Under <c>Sangam:Policy</c>: <c>MinPasswordLength</c>
/// (never below the built-in 12) and <c>Mfa</c>. Under <c>Sangam:Passwords:BreachCheck</c>: <c>Enabled</c> (default
/// on), <c>RequiredForEveryone</c> (default on: every password set and every password sign-in is checked, ASVS
/// V2.1.7), <c>Endpoint</c> (the Pwned Passwords range service; empty uses only the built-in list) and
/// <c>TimeoutSeconds</c>.
/// </summary>
public sealed class PolicySettings
{
    /// <summary>The range service used unless <c>Sangam:Passwords:BreachCheck:Endpoint</c> says otherwise.</summary>
    public static readonly Uri DefaultBreachCheckEndpoint = new("https://api.pwnedpasswords.com/range/");

    /// <summary>The platform's own policy.</summary>
    public SecurityPolicy Platform { get; init; } = new(SignInPolicy.Default, PasswordStrength.MinimumLength, MfaRequirement.Optional, true);

    /// <summary>Whether the breached-password check is switched on.</summary>
    public bool BreachCheckEnabled { get; init; } = true;

    /// <summary>The k-anonymity range service, or <see langword="null"/> to use only the built-in list.</summary>
    public Uri? BreachCheckEndpoint { get; init; } = DefaultBreachCheckEndpoint;

    /// <summary>How long to wait for the range service before falling back to the built-in list.</summary>
    public TimeSpan BreachCheckTimeout { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>Reads the settings.</summary>
    /// <param name="configuration">Configuration.</param>
    public static PolicySettings From(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        bool enabled = configuration.GetValue("Sangam:Passwords:BreachCheck:Enabled", true);
        int minLength = Math.Clamp(configuration.GetValue("Sangam:Policy:MinPasswordLength", PasswordStrength.MinimumLength), PasswordStrength.MinimumLength, SecurityPolicy.MaxMinPasswordLength);
        MfaRequirement mfa = Enum.TryParse(configuration["Sangam:Policy:Mfa"], ignoreCase: true, out MfaRequirement m) ? m : MfaRequirement.Optional;
        bool everyone = enabled && configuration.GetValue("Sangam:Passwords:BreachCheck:RequiredForEveryone", true);
        string? endpoint = configuration["Sangam:Passwords:BreachCheck:Endpoint"];
        int timeout = Math.Clamp(configuration.GetValue("Sangam:Passwords:BreachCheck:TimeoutSeconds", 3), 1, 30);
        return new PolicySettings
        {
            Platform = new SecurityPolicy(SignInPolicy.Default, minLength, mfa, everyone),
            BreachCheckEnabled = enabled,
            BreachCheckEndpoint = endpoint is null
                ? DefaultBreachCheckEndpoint
                : Uri.TryCreate(endpoint.EndsWith('/') ? endpoint : endpoint + "/", UriKind.Absolute, out Uri? uri) && uri.Scheme == Uri.UriSchemeHttps ? uri : null,
            BreachCheckTimeout = TimeSpan.FromSeconds(timeout),
        };
    }
}
