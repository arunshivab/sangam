using Microsoft.Extensions.Configuration;
using Sangam.Identity.Application.Security;
using Sangam.Identity.Domain;
using Sangam.Identity.Domain.Enums;

namespace Sangam.Identity.Infrastructure.Policies;

/// <summary>
/// Platform policy and breached-password settings (PR-16). Under <c>Sangam:Policy</c>: <c>MinPasswordLength</c>
/// (never below the built-in 8) and <c>Mfa</c>. Under <c>Sangam:Passwords:BreachCheck</c>: <c>Enabled</c>,
/// <c>RequiredForEveryone</c> and <c>ListPath</c> — the offline list (D-J: nothing leaves the server; the check
/// stays off until the list is loaded).
/// </summary>
public sealed class PolicySettings
{
    /// <summary>The platform's own policy.</summary>
    public SecurityPolicy Platform { get; init; } = new(SignInPolicy.Default, PasswordStrength.MinimumLength, MfaRequirement.Optional, false);

    /// <summary>Whether the breached-password check is switched on (it runs only once its list is loaded).</summary>
    public bool BreachCheckEnabled { get; init; }

    /// <summary>The offline list file (<see cref="PwnedPasswordList"/>), or <see langword="null"/> when none is set.</summary>
    public string? BreachListPath { get; init; }

    /// <summary>Reads the settings.</summary>
    /// <param name="configuration">Configuration.</param>
    public static PolicySettings From(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        bool enabled = configuration.GetValue("Sangam:Passwords:BreachCheck:Enabled", false);
        int minLength = Math.Clamp(configuration.GetValue("Sangam:Policy:MinPasswordLength", PasswordStrength.MinimumLength), PasswordStrength.MinimumLength, SecurityPolicy.MaxMinPasswordLength);
        MfaRequirement mfa = Enum.TryParse(configuration["Sangam:Policy:Mfa"], ignoreCase: true, out MfaRequirement m) ? m : MfaRequirement.Optional;
        bool everyone = enabled && configuration.GetValue("Sangam:Passwords:BreachCheck:RequiredForEveryone", false);
        string? listPath = configuration["Sangam:Passwords:BreachCheck:ListPath"];
        return new PolicySettings
        {
            Platform = new SecurityPolicy(SignInPolicy.Default, minLength, mfa, everyone),
            BreachCheckEnabled = enabled,
            BreachListPath = string.IsNullOrWhiteSpace(listPath) ? null : listPath,
        };
    }
}
