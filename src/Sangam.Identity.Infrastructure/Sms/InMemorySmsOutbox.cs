using Microsoft.Extensions.Logging;
using Sangam.Identity.Application.Abstractions;
using Sangam.Identity.Infrastructure.Services;

namespace Sangam.Identity.Infrastructure.Sms;

/// <summary>
/// Development and Testing <see cref="ISmsSender"/>: keeps the last messages in memory for
/// <c>/dev/outbox</c> and the tests. It logs only the template and a masked number — never the text,
/// which carries the code (OI-038). The start-up guard refuses it in any other environment.
/// </summary>
public sealed partial class InMemorySmsOutbox : ISmsSender
{
    /// <summary>How many messages are kept.</summary>
    public const int Capacity = 50;

    private readonly Lock _gate = new();
    private readonly LinkedList<SentSms> _messages = new();
    private readonly ILogger<InMemorySmsOutbox> _logger;
    private readonly DevOutboxStore? _shared;

    /// <summary>Initialises the outbox.</summary>
    /// <param name="logger">Logger.</param>
    /// <param name="shared">The outbox all hosts share in Development (V-11), when switched on.</param>
    public InMemorySmsOutbox(ILogger<InMemorySmsOutbox> logger, DevOutboxStore? shared = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _shared = shared;
    }

    /// <inheritdoc />
    public string Name => SmsSettings.OutboxProvider;

    /// <summary>When set, every message is refused, so tests can exercise provider failure.</summary>
    public bool Refuse { get; set; }

    /// <summary>Gets the kept messages, newest first.</summary>
    public IReadOnlyList<SentSms> Recent
    {
        get
        {
            lock (_gate)
            {
                return [.. _messages];
            }
        }
    }

    /// <inheritdoc />
    public async Task<SmsSendResult> SendAsync(OutgoingSms message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (Refuse)
        {
            return new SmsSendResult(false, Name, Error: "refused by the test outbox");
        }

        string id = "outbox-" + Guid.NewGuid().ToString("N");
        lock (_gate)
        {
            _messages.AddFirst(new SentSms(DateTimeOffset.UtcNow, id, message));
            while (_messages.Count > Capacity)
            {
                _messages.RemoveLast();
            }
        }

        if (_logger.IsEnabled(LogLevel.Information))
        {
            string masked = SmsNumbers.Mask(message.ToE164);
            LogSms(message.TemplateKey, masked);
        }

        if (_shared is not null)
        {
            await _shared.RecordAsync("sms", message.ToE164, message.TemplateKey + " · " + message.SenderHeader, message.Text, cancellationToken).ConfigureAwait(false);
        }

        return new SmsSendResult(true, Name, id);
    }

    /// <summary>The newest message to <paramref name="toE164"/>, or <see langword="null"/>.</summary>
    /// <param name="toE164">Number in E.164 form.</param>
    public SentSms? LatestFor(string toE164)
    {
        ArgumentNullException.ThrowIfNull(toE164);
        lock (_gate)
        {
            return _messages.FirstOrDefault(m => string.Equals(m.Message.ToE164, toE164, StringComparison.Ordinal));
        }
    }

    [LoggerMessage(EventId = 1101, Level = LogLevel.Information, Message = "SMS {Template} captured in the development outbox for {To}; the text is not logged")]
    private partial void LogSms(string template, string to);
}

/// <summary>A message captured by the outbox.</summary>
/// <param name="SentAt">When it was captured (UTC).</param>
/// <param name="ProviderMessageId">The outbox's id for it.</param>
/// <param name="Message">The message.</param>
public sealed record SentSms(DateTimeOffset SentAt, string ProviderMessageId, OutgoingSms Message);
