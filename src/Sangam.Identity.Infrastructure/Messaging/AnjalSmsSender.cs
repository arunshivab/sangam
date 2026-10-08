using Microsoft.Extensions.Logging;
using Sangam.Identity.Application.Abstractions;
using Sangam.Identity.Infrastructure.Sms;

namespace Sangam.Identity.Infrastructure.Messaging;

/// <summary>
/// Sends SMS through Anjal's API (D-M: Anjal is the single messaging gateway; it reaches the Indian aggregators and
/// fails over between two of them). Sangam keeps the DLT template ids and its per-number and per-IP limits, and
/// sends the header, the DLT template id and the exact registered text. The person is waiting, so few attempts.
/// </summary>
public sealed partial class AnjalSmsSender : ISmsSender
{
    /// <summary>The provider name in settings and in <c>sms_messages.provider</c>.</summary>
    public const string ProviderName = "anjal";

    private readonly AnjalClient _client;
    private readonly AnjalOptions _options;
    private readonly ILogger<AnjalSmsSender> _logger;

    /// <summary>Initialises the sender.</summary>
    /// <param name="client">Anjal's API client.</param>
    /// <param name="options">Anjal settings.</param>
    /// <param name="logger">Logger.</param>
    public AnjalSmsSender(AnjalClient client, AnjalOptions options, ILogger<AnjalSmsSender> logger)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public string Name => ProviderName;

    /// <inheritdoc />
    public async Task<SmsSendResult> SendAsync(OutgoingSms message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        string masked = SmsNumbers.Mask(message.ToE164);
        if (!_options.IsAllowed(message.ToE164))
        {
            LogBlocked(masked);
            MessagingMetrics.SmsRefused.Add(1);
            return new SmsSendResult(false, Name, Error: "not on the staging allowlist");
        }

        AnjalSmsRequest body = new(message.ToE164, message.SenderHeader, message.DltTemplateId, message.Text, message.TemplateKey);
        AnjalResult result = await _client.PostAsync(_options.SmsPath, body, _options.SmsAttempts, masked, cancellationToken).ConfigureAwait(false);
        (result.Accepted ? MessagingMetrics.SmsAccepted : MessagingMetrics.SmsRefused).Add(1);
        return result.Accepted
            ? new SmsSendResult(true, Name, result.MessageId)
            : new SmsSendResult(false, Name, Error: result.Error);
    }

    [LoggerMessage(EventId = 1321, Level = LogLevel.Warning, Message = "SMS to {To} refused: not on the staging allowlist")]
    private partial void LogBlocked(string to);
}

/// <summary>The body of Anjal's send-an-SMS request (docs/anjal-messaging-contract.md).</summary>
/// <param name="To">The number in E.164 form.</param>
/// <param name="Header">The registered six-letter header (sender id).</param>
/// <param name="DltTemplateId">The DLT content template id.</param>
/// <param name="Text">The text exactly as registered, with its variables filled in.</param>
/// <param name="TemplateKey">Sangam's template key, for Anjal's reports.</param>
public sealed record AnjalSmsRequest(string To, string Header, string DltTemplateId, string Text, string TemplateKey);
