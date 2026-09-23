namespace Sangam.Identity.Application.Abstractions;

/// <summary>
/// Sends transactional email (verification OTPs, password resets, security notices).
/// Production implementation talks to Anjal; development logs the message.
/// </summary>
public interface IEmailSender
{
    /// <summary>Sends one message.</summary>
    /// <param name="message">The message.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default);
}
