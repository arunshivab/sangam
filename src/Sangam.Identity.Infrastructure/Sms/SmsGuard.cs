using Microsoft.Extensions.Configuration;

namespace Sangam.Identity.Infrastructure.Sms;

/// <summary>
/// Start-up rule for SMS (PR-15). Outside Development and Testing, when SMS is switched on: the development
/// outbox is refused, the provider must have an adapter in this build, numbers must be hashed with a real
/// key, the header must be six letters, and every template must carry its DLT id and a well-formed text.
/// </summary>
public static class SmsGuard
{
    /// <summary>Provider names this build has adapters for, apart from the development outbox.</summary>
    /// <remarks>None yet: the provider is the founder's decision (SGM-206 open question 1). Its adapter is added with that decision.</remarks>
    public static IReadOnlySet<string> ProductionProviders { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Returns why the host must not start, or <see langword="null"/> when SMS is safe or switched off.</summary>
    /// <param name="environmentName">The host environment name.</param>
    /// <param name="configuration">Configuration.</param>
    public static string? Validate(string environmentName, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(environmentName);
        ArgumentNullException.ThrowIfNull(configuration);
        SmsSettings settings = SmsSettings.From(configuration);
        if (!settings.Enabled)
        {
            return null;
        }

        foreach ((string key, SmsTemplateSettings template) in settings.Templates)
        {
            if (DltTemplate.VariableCount(template.Text) == 0)
            {
                return $"SMS template '{key}' has no {DltTemplate.Placeholder} for the code. Refusing to start.";
            }
        }

        bool development = string.Equals(environmentName, "Development", StringComparison.OrdinalIgnoreCase)
            || string.Equals(environmentName, "Testing", StringComparison.OrdinalIgnoreCase);
        if (development)
        {
            return null;
        }

        string where = $"in the '{environmentName}' environment";
        foreach (string provider in new[] { settings.Provider, settings.FailoverProvider ?? string.Empty }.Where(p => p.Length > 0))
        {
            if (string.Equals(provider, SmsSettings.OutboxProvider, StringComparison.OrdinalIgnoreCase))
            {
                return $"The development SMS outbox is configured {where}. Refusing to start: it would expose one-time codes.";
            }

            if (!ProductionProviders.Contains(provider))
            {
                return $"SMS provider '{provider}' has no adapter in this build {where}. Refusing to start; switch SMS off (Sangam:Sms:Enabled=false) until the provider's adapter is added.";
            }
        }

        if (settings.Provider.Length == 0)
        {
            return $"SMS is enabled {where} but no provider is configured (Sangam:Sms:Provider). Refusing to start.";
        }

        if (string.IsNullOrWhiteSpace(settings.HashKey) || settings.HashKey.Length < 32)
        {
            return $"SMS is enabled {where} without a hash key of at least 32 characters (Sangam:Sms:HashKey). Refusing to start: numbers would be hashed with a public key.";
        }

        if (settings.SenderHeader.Length != 6 || !settings.SenderHeader.All(char.IsAsciiLetterUpper))
        {
            return $"The SMS header '{settings.SenderHeader}' is not six capital letters (Sangam:Sms:SenderHeader). Refusing to start.";
        }

        foreach ((string key, SmsTemplateSettings template) in settings.Templates)
        {
            if (string.IsNullOrWhiteSpace(template.Id))
            {
                return $"SMS template '{key}' has no DLT template id (Sangam:Sms:Templates:{key}:Id) {where}. Operators refuse unregistered text. Refusing to start.";
            }
        }

        return null;
    }
}
