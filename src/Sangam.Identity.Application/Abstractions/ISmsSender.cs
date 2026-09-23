namespace Sangam.Identity.Application.Abstractions;

/// <summary>
/// Sends SMS (mobile OTP). Reserved: no implementation is registered until a DLT-registered
/// provider is chosen; mobile verification ships next year.
/// </summary>
public interface ISmsSender
{
    /// <summary>Sends one message to an E.164 number.</summary>
    /// <param name="toE164">Destination number in E.164 form.</param>
    /// <param name="text">Message text.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task SendAsync(string toE164, string text, CancellationToken cancellationToken = default);
}
