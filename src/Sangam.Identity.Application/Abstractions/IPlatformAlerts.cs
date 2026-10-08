namespace Sangam.Identity.Application.Abstractions;

/// <summary>
/// Alerts the platform's owner (the founder) by e-mail and SMS through Anjal (D-H, D-K): an urgent support
/// override, a health check failing, a threshold crossed. No third party is involved.
/// </summary>
public interface IPlatformAlerts
{
    /// <summary>Sends one alert to every alert recipient.</summary>
    /// <param name="summary">What happened, in a few words (the SMS carries the first 30 characters).</param>
    /// <param name="details">More detail for the e-mail. Never personal data beyond an account id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task SendAsync(string summary, string details, CancellationToken cancellationToken = default);
}
