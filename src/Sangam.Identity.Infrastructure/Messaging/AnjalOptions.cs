using Microsoft.Extensions.Configuration;

namespace Sangam.Identity.Infrastructure.Messaging;

/// <summary>
/// Settings for Anjal's messaging API under <c>Sangam:Anjal</c> (D-B, D-M). Anjal sends all of Sangam's e-mail
/// and, once DLT is registered, its SMS. Anjal's API details follow its own testing, so the address, the paths,
/// how the key is presented and the retry rules are all settings; the request bodies are the contract proposed
/// in <c>docs/anjal-messaging-contract.md</c>. Sangam to Anjal is machine-to-machine: an API key, no user.
/// </summary>
public sealed class AnjalOptions
{
    /// <summary>Configuration section.</summary>
    public const string SectionName = "Sangam:Anjal";

    /// <summary>Anjal's API base address, for example <c>https://api.anjalmail.com/</c>. Unset: no Anjal sender.</summary>
    public string? BaseUrl { get; set; }

    /// <summary>The API key. A secret: from a secret file in production, never a settings file.</summary>
    public string? ApiKey { get; set; }

    /// <summary>The header that carries the key (default <c>Authorization</c>).</summary>
    public string ApiKeyHeader { get; set; } = "Authorization";

    /// <summary>The scheme in front of the key in <see cref="ApiKeyHeader"/> (default <c>Bearer</c>; empty for the bare key).</summary>
    public string ApiKeyScheme { get; set; } = "Bearer";

    /// <summary>Path of the send-an-e-mail endpoint, relative to <see cref="BaseUrl"/>.</summary>
    public string EmailPath { get; set; } = "api/v1/messages/email";

    /// <summary>Path of the send-an-SMS endpoint, relative to <see cref="BaseUrl"/>.</summary>
    public string SmsPath { get; set; } = "api/v1/messages/sms";

    /// <summary>The sender address, on a domain whose SPF, DKIM and DMARC pass for Anjal (go-live checklist).</summary>
    public string FromAddress { get; set; } = "no-reply@sangamid.in";

    /// <summary>The sender name.</summary>
    public string FromName { get; set; } = "SangamID";

    /// <summary>
    /// Staging allowlist: comma-separated addresses (<c>person@example.com</c>), domains (<c>@example.com</c>) or
    /// mobile numbers (<c>+919876543210</c>). When set, anything else is refused before it reaches Anjal, so staging
    /// can never reach real people. Empty in production.
    /// </summary>
    public string? AllowedRecipients { get; set; }

    /// <summary>Timeout for one HTTP attempt, in seconds.</summary>
    public int TimeoutSeconds { get; set; } = 15;

    /// <summary>Attempts for one e-mail (sent in the background, so it may wait): the first plus retries.</summary>
    public int EmailAttempts { get; set; } = 5;

    /// <summary>Attempts for one SMS (the person is waiting on the page): the first plus retries.</summary>
    public int SmsAttempts { get; set; } = 2;

    /// <summary>The pause before the first retry; it doubles each time, with jitter, up to <see cref="MaxRetryDelay"/>.</summary>
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>The longest pause between attempts, including one Anjal asks for with <c>Retry-After</c>.</summary>
    public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Whether an Anjal sender is configured.</summary>
    public bool Configured => !string.IsNullOrWhiteSpace(BaseUrl);

    /// <summary>Reads the settings.</summary>
    /// <param name="configuration">Configuration.</param>
    public static AnjalOptions From(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        AnjalOptions options = new();
        configuration.GetSection(SectionName).Bind(options);
        return options;
    }

    /// <summary>Whether <paramref name="recipient"/> (an address or a number) may be sent to under the allowlist.</summary>
    /// <param name="recipient">The e-mail address or E.164 number.</param>
    public bool IsAllowed(string recipient)
    {
        ArgumentNullException.ThrowIfNull(recipient);
        if (string.IsNullOrWhiteSpace(AllowedRecipients))
        {
            return true;
        }

        return AllowedRecipients.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(a => a.StartsWith('@')
                ? recipient.EndsWith(a, StringComparison.OrdinalIgnoreCase)
                : string.Equals(recipient, a, StringComparison.OrdinalIgnoreCase));
    }
}
