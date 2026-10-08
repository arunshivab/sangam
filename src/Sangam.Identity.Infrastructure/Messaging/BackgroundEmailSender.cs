using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Sangam.Identity.Application.Abstractions;
using Sangam.Identity.Infrastructure.Services;

namespace Sangam.Identity.Infrastructure.Messaging;

/// <summary>
/// Hands e-mail to Anjal in the background (D-B). A page never waits for Anjal or its retries, so a slow or
/// unreachable Anjal cannot hold up a sign-in, and the response takes the same time whether or not a message was
/// sent — which keeps "no account with that address" indistinguishable (D-L). The queue is in memory: a message
/// still queued when the host stops is lost, which suits one-time codes (they expire in minutes); the count of
/// failed hand-offs is on the monitoring page (D-H).
/// </summary>
public sealed partial class BackgroundEmailSender : BackgroundService, IEmailSender
{
    /// <summary>How many messages may wait at once; beyond that a message is dropped and counted as failed.</summary>
    public const int Capacity = 10_000;

    private readonly Channel<EmailMessage> _queue = Channel.CreateBounded<EmailMessage>(new BoundedChannelOptions(Capacity)
    {
        FullMode = BoundedChannelFullMode.DropWrite,
        SingleReader = true,
    });

    private readonly IEmailSender _inner;
    private readonly ILogger<BackgroundEmailSender> _logger;

    /// <summary>Initialises the sender.</summary>
    /// <param name="inner">The sender that talks to Anjal (<see cref="AnjalEmailSender"/>).</param>
    /// <param name="logger">Logger.</param>
    public BackgroundEmailSender(IEmailSender inner, ILogger<BackgroundEmailSender> logger)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Messages waiting to be handed to Anjal.</summary>
    public int Pending => _queue.Reader.CanCount ? _queue.Reader.Count : 0;

    /// <inheritdoc />
    public Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (!_queue.Writer.TryWrite(message))
        {
            MessagingMetrics.EmailsFailed.Add(1);
            LogDropped(LogRedaction.MaskEmail(message.ToEmail));
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (EmailMessage message in _queue.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await _inner.SendAsync(message, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (InvalidOperationException ex)
            {
                // Counted by the Anjal sender; logged here without content.
                LogFailed(LogRedaction.MaskEmail(message.ToEmail), ex.Message);
            }
        }
    }

    [LoggerMessage(EventId = 1312, Level = LogLevel.Error, Message = "E-mail to {To} was not sent: {Reason}")]
    private partial void LogFailed(string to, string reason);

    [LoggerMessage(EventId = 1313, Level = LogLevel.Error, Message = "E-mail to {To} dropped: the queue to Anjal is full")]
    private partial void LogDropped(string to);
}
