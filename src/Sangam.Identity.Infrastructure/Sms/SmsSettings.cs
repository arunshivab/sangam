using Microsoft.Extensions.Configuration;

namespace Sangam.Identity.Infrastructure.Sms;

/// <summary>
/// SMS settings under <c>Sangam:Sms</c> (PR-15, SGM-206). The provider, the header and the DLT template ids
/// are the founder's decisions (SGM-206 open questions 1–3); until they are made SMS stays off outside
/// Development and Testing.
/// </summary>
public sealed class SmsSettings
{
    /// <summary>Configuration section.</summary>
    public const string SectionName = "Sangam:Sms";

    /// <summary>The development outbox provider name: messages are kept in memory for <c>/dev/outbox</c> and the tests.</summary>
    public const string OutboxProvider = "outbox";

    /// <summary>Template key: sign-in code.</summary>
    public const string SignInTemplate = "sign_in";

    /// <summary>Template key: mobile verification.</summary>
    public const string MobileVerificationTemplate = "mobile_verification";

    /// <summary>Template key: step-up confirmation of an action in an application.</summary>
    public const string StepUpTemplate = "step_up";

    private const string DevelopmentHashKey = "sangam-development-sms-hash-key-not-for-production";

    /// <summary>Whether SMS is offered at all.</summary>
    public bool Enabled { get; set; }

    /// <summary>The primary provider's name; it must have an adapter in this build.</summary>
    public string Provider { get; set; } = string.Empty;

    /// <summary>An optional second provider tried when the first refuses a message.</summary>
    public string? FailoverProvider { get; set; }

    /// <summary>The registered six-character header (sender id).</summary>
    public string SenderHeader { get; set; } = "SANGAM";

    /// <summary>Comma-separated dialling codes SMS is offered for (default India only).</summary>
    public string AllowedCountryCodes { get; set; } = "+91";

    /// <summary>Key for the keyed hashes of numbers and IP addresses. Required outside Development and Testing.</summary>
    public string? HashKey { get; set; }

    /// <summary>At most this many messages an hour to one number, whichever account asks.</summary>
    public int PerNumberPerHour { get; set; } = 5;

    /// <summary>At most this many messages an hour requested from one IP address.</summary>
    public int PerIpPerHour { get; set; } = 10;

    /// <summary>When the day's (UTC) messages reach this number, an alert is logged and audited once. Unset: no alert.</summary>
    public int? DailyAlertThreshold { get; set; }

    /// <summary>Shared secret a provider's delivery-report webhook must present. Unset: the webhook is off.</summary>
    public string? DeliveryReportToken { get; set; }

    /// <summary>The DLT templates by key. Each needs the registered id and the registered text.</summary>
    public Dictionary<string, SmsTemplateSettings> Templates { get; set; } = new(StringComparer.Ordinal);

    /// <summary>The key used for hashing; a fixed development key when none is set.</summary>
    public string EffectiveHashKey => string.IsNullOrWhiteSpace(HashKey) ? DevelopmentHashKey : HashKey;

    /// <summary>The dialling codes as a list.</summary>
    public IReadOnlyList<string> CountryCodes
        => [.. AllowedCountryCodes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    /// <summary>Reads the settings, filling in the SGM-206 template texts where none are configured.</summary>
    /// <param name="configuration">Configuration.</param>
    public static SmsSettings From(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        SmsSettings settings = new();
        configuration.GetSection(SectionName).Bind(settings);
        foreach ((string key, string text) in DefaultTexts)
        {
            if (!settings.Templates.TryGetValue(key, out SmsTemplateSettings? template))
            {
                settings.Templates[key] = new SmsTemplateSettings { Text = text };
            }
            else if (string.IsNullOrWhiteSpace(template.Text))
            {
                template.Text = text;
            }
        }

        return settings;
    }

    /// <summary>The English texts from SGM-206 §3. Each language version is registered as its own template (SGM-209).</summary>
    public static IReadOnlyDictionary<string, string> DefaultTexts { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [SignInTemplate] = "{#var#} is your SangamID sign-in code. It expires in 10 minutes. Do not share it. - SANGAM",
        [MobileVerificationTemplate] = "{#var#} is your code to verify this mobile for SangamID. - SANGAM",
        [StepUpTemplate] = "{#var#} is your SangamID code to confirm an action in {#var#}. - SANGAM",
    };
}

/// <summary>One DLT content template.</summary>
public sealed class SmsTemplateSettings
{
    /// <summary>The template id on the DLT platform.</summary>
    public string? Id { get; set; }

    /// <summary>The registered text, with <c>{#var#}</c> for each variable.</summary>
    public string Text { get; set; } = string.Empty;
}
