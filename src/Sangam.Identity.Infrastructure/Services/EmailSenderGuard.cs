using Microsoft.Extensions.Configuration;

namespace Sangam.Identity.Infrastructure.Services;

/// <summary>
/// Start-up rule for e-mail (OI-038): outside Development and Testing the development outbox must
/// be off, because it would expose one-time codes, and a real sender must be configured, because
/// codes cannot otherwise be delivered.
/// </summary>
public static class EmailSenderGuard
{
    /// <summary>Configuration key that switches on the development outbox.</summary>
    public const string OutboxKey = "Sangam:Email:UseOutbox";

    /// <summary>Configuration key of the SMTP host of a real sender (added by PR-09).</summary>
    public const string SmtpHostKey = "Sangam:Email:Smtp:Host";

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

        if (configuration.GetValue<bool>(OutboxKey))
        {
            return $"The development e-mail outbox ({OutboxKey}) is enabled in the '{environmentName}' environment. Refusing to start: it would expose one-time codes.";
        }

        if (string.IsNullOrWhiteSpace(configuration[SmtpHostKey]))
        {
            return $"No e-mail sender is configured ({SmtpHostKey}) in the '{environmentName}' environment. One-time codes could not be delivered. Refusing to start.";
        }

        return null;
    }
}
