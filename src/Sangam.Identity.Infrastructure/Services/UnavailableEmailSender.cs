using Sangam.Identity.Application.Abstractions;

namespace Sangam.Identity.Infrastructure.Services;

/// <summary>
/// The <see cref="IEmailSender"/> used when no real sender is configured. It refuses to send and
/// never writes a recipient, subject or body anywhere: one-time codes must not reach a log (OI-038).
/// The identity server refuses to start in this state outside Development and Testing
/// (<see cref="EmailSenderGuard"/>); the Anjal-backed sender arrives in PR-09.
/// </summary>
public sealed class UnavailableEmailSender : IEmailSender
{
    /// <summary>The message of the exception thrown on every send.</summary>
    public const string Refusal = "No e-mail sender is configured (Sangam:Email). The message was not sent.";

    /// <inheritdoc />
    public Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        throw new InvalidOperationException(Refusal);
    }
}
