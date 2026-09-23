using Microsoft.Extensions.Logging;
using Sangam.Identity.Application.Abstractions;

namespace Sangam.Identity.Infrastructure.Services;

/// <summary>
/// Development-only <see cref="IEmailSender"/>: writes the message to the log instead of
/// sending it. The Anjal-backed sender replaces this once Anjal is hosted.
/// </summary>
public sealed partial class LoggingEmailSender : IEmailSender
{
    private readonly ILogger<LoggingEmailSender> _logger;

    /// <summary>Initialises the sender.</summary>
    /// <param name="logger">Logger.</param>
    public LoggingEmailSender(ILogger<LoggingEmailSender> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        LogEmail(message.ToEmail, message.Subject, message.TextBody);
        return Task.CompletedTask;
    }

    [LoggerMessage(EventId = 1000, Level = LogLevel.Information, Message = "EMAIL (not sent) to {To}: {Subject}\n{Body}")]
    private partial void LogEmail(string to, string subject, string body);
}
