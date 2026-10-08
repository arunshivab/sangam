using Microsoft.Extensions.Logging;
using Sangam.Identity.Application.Abstractions;

namespace Sangam.Identity.Infrastructure.Services;

/// <summary>
/// Development/Testing <see cref="IEmailSender"/>: keeps the last <see cref="Capacity"/> messages
/// in memory so the <c>/dev/outbox</c> page and the tests can read verification codes without a
/// mail server, and logs each one. Never registered in Production.
/// </summary>
public sealed partial class InMemoryEmailOutbox : IEmailSender
{
    /// <summary>How many messages are kept.</summary>
    public const int Capacity = 50;

    private readonly Lock _gate = new();
    private readonly LinkedList<SentEmail> _messages = new();
    private readonly ILogger<InMemoryEmailOutbox> _logger;

    /// <summary>Initialises the outbox.</summary>
    /// <param name="logger">Logger.</param>
    public InMemoryEmailOutbox(ILogger<InMemoryEmailOutbox> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Gets the kept messages, newest first.</summary>
    public IReadOnlyList<SentEmail> Recent
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
    public Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        SentEmail sent = new(DateTimeOffset.UtcNow, message);
        lock (_gate)
        {
            _messages.AddFirst(sent);
            while (_messages.Count > Capacity)
            {
                _messages.RemoveLast();
            }
        }

        if (_logger.IsEnabled(LogLevel.Information))
        {
            string masked = LogRedaction.MaskEmail(message.ToEmail);
            LogEmail(masked);
        }

        return Task.CompletedTask;
    }

    /// <summary>The newest message sent to <paramref name="email"/>, or <see langword="null"/>.</summary>
    /// <param name="email">Recipient address (case-insensitive).</param>
    public SentEmail? LatestFor(string email)
    {
        ArgumentNullException.ThrowIfNull(email);
        lock (_gate)
        {
            return _messages.FirstOrDefault(m => string.Equals(m.Message.ToEmail, email, StringComparison.OrdinalIgnoreCase));
        }
    }

    // Subjects carry the one-time code ("123456 is your Sangam … code"), so only a masked
    // recipient is logged — never the subject or body (OI-038).
    [LoggerMessage(EventId = 1001, Level = LogLevel.Information, Message = "EMAIL captured in the development outbox for {To}; subject and body are not logged")]
    private partial void LogEmail(string to);
}

/// <summary>A message captured by the outbox.</summary>
/// <param name="SentAt">When it was captured (UTC).</param>
/// <param name="Message">The message.</param>
public sealed record SentEmail(DateTimeOffset SentAt, EmailMessage Message);
