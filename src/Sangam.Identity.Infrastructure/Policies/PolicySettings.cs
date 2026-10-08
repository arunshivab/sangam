using Microsoft.Extensions.Configuration;
using Sangam.Identity.Application.Security;
using Sangam.Identity.Domain;
using Sangam.Identity.Domain.Enums;

namespace Sangam.Identity.Infrastructure.Policies;

/// <summary>
/// Platform policy and breached-password settings (PR-16). Under <c>Sangam:Policy</c>: <c>MinPasswordLength</c>
/// (never below the built-in 8) and <c>Mfa</c>. Under <c>Sangam:Passwords:BreachCheck</c>: <c>Enabled</c> (the
/// service may be called — the founder's decision, because it sends five characters of a password's SHA-1 hash
/// to a third party), <c>RequiredForEveryone</c>, <c>Endpoint</c> and <c>TimeoutSeconds</c>.
/// </summary>
public sealed class PolicySettings
{
    /// <summary>The default range endpoint (Pwned Passwords, k-anonymity).</summary>
    public const string DefaultEndpoint = "https://api.pwnedpasswords.com/range/";

    /// <summary>The platform's own policy.</summary>
    public SecurityPolicy Platform { get; init; } = new(SignInPolicy.Default, PasswordStrength.MinimumLength, MfaRequirement.Optional, false);

    /// <summary>Whether the breach-check service may be called.</summary>
    public bool BreachCheckEnabled { get; init; }

    /// <summary>The range endpoint; the first five hex characters of the SHA-1 are appended.</summary>
    public Uri BreachCheckEndpoint { get; init; } = new(DefaultEndpoint);

    /// <summary>How long to wait for the service before giving up (and not blocking the person).</summary>
    public TimeSpan BreachCheckTimeout { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>Reads the settings.</summary>
    /// <param name="configuration">Configuration.</param>
    public static PolicySettings From(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        bool enabled = configuration.GetValue("Sangam:Passwords:BreachCheck:Enabled", false);
        int minLength = Math.Clamp(configuration.GetValue("Sangam:Policy:MinPasswordLength", PasswordStrength.MinimumLength), PasswordStrength.MinimumLength, SecurityPolicy.MaxMinPasswordLength);
        MfaRequirement mfa = Enum.TryParse(configuration["Sangam:Policy:Mfa"], ignoreCase: true, out MfaRequirement m) ? m : MfaRequirement.Optional;
        bool everyone = enabled && configuration.GetValue("Sangam:Passwords:BreachCheck:RequiredForEveryone", false);
        string endpoint = configuration["Sangam:Passwords:BreachCheck:Endpoint"] ?? DefaultEndpoint;
        return new PolicySettings
        {
            Platform = new SecurityPolicy(SignInPolicy.Default, minLength, mfa, everyone),
            BreachCheckEnabled = enabled,
            BreachCheckEndpoint = new Uri(endpoint.EndsWith('/') ? endpoint : endpoint + "/"),
            BreachCheckTimeout = TimeSpan.FromSeconds(Math.Clamp(configuration.GetValue("Sangam:Passwords:BreachCheck:TimeoutSeconds", 3), 1, 30)),
        };
    }
}
