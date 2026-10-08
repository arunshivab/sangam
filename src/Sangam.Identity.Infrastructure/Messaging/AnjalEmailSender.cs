using Microsoft.Extensions.Logging;
using Sangam.Identity.Application.Abstractions;
using Sangam.Identity.Infrastructure.Services;

namespace Sangam.Identity.Infrastructure.Messaging;

/// <summary>
/// Sends Sangam's e-mail through Anjal's API (D-B: Anjal sends all of Sangam's e-mail from day one, by its API,
/// not SMTP). Applies the staging allowlist before anything leaves, and throws when Anjal does not accept the
/// message after every attempt, so the caller (normally <see cref="BackgroundEmailSender"/>) can count it.
/// </summary>
public sealed partial class AnjalEmailSender : IEmailSender
{
    private readonly AnjalClient _client;
    private readonly AnjalOptions _options;
    private readonly ILogger<AnjalEmailSender> _logger;

    /// <summary>Initialises the sender.</summary>
    /// <param name="client">Anjal's API client.</param>
    /// <param name="options">Anjal settings.</param>
    /// <param name="logger">Logger.</param>
    public AnjalEmailSender(AnjalClient client, AnjalOptions options, ILogger<AnjalEmailSender> logger)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        string masked = LogRedaction.MaskEmail(message.ToEmail);
        if (!_options.IsAllowed(message.ToEmail))
        {
            LogBlocked(masked);
            throw new InvalidOperationException("The recipient is not on the staging allowlist (Sangam:Anjal:AllowedRecipients).");
        }

        AnjalEmailRequest body = new(
            new AnjalAddress(_options.FromAddress, _options.FromName),
            new AnjalAddress(message.ToEmail, message.ToName),
            message.Subject,
            message.TextBody,
            message.HtmlBody);
        AnjalResult result = await _client.PostAsync(_options.EmailPath, body, _options.EmailAttempts, masked, cancellationToken).ConfigureAwait(false);
        if (!result.Accepted)
        {
            MessagingMetrics.EmailsFailed.Add(1);
            throw new InvalidOperationException($"Anjal did not accept the e-mail ({result.Error}, {result.Attempts} attempt(s)).");
        }

        MessagingMetrics.EmailsSent.Add(1);
    }

    [LoggerMessage(EventId = 1311, Level = LogLevel.Warning, Message = "E-mail to {To} refused: not on the staging allowlist")]
    private partial void LogBlocked(string to);
}

/// <summary>An address with a display name, in Anjal's request.</summary>
/// <param name="Address">The e-mail address.</param>
/// <param name="Name">The display name.</param>
public sealed record AnjalAddress(string Address, string Name);

/// <summary>The body of Anjal's send-an-e-mail request (docs/anjal-messaging-contract.md).</summary>
/// <param name="From">The sender.</param>
/// <param name="To">The recipient.</param>
/// <param name="Subject">The subject.</param>
/// <param name="Text">The plain-text body.</param>
/// <param name="Html">The HTML body, when there is one.</param>
public sealed record AnjalEmailRequest(AnjalAddress From, AnjalAddress To, string Subject, string Text, string? Html);
