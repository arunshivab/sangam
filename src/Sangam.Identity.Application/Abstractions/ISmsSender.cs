namespace Sangam.Identity.Application.Abstractions;

/// <summary>An SMS ready for a DLT-registered provider (SGM-206 §4).</summary>
/// <param name="ToE164">Destination number in E.164 form, for example <c>+919876543210</c>.</param>
/// <param name="TemplateKey">Sangam's template key, for example <c>sign_in</c>.</param>
/// <param name="DltTemplateId">The template's id on the DLT platform; providers in India refuse unregistered text.</param>
/// <param name="SenderHeader">The registered six-character header, for example <c>SANGAM</c>.</param>
/// <param name="Text">The rendered text, exactly as registered with its variables filled in.</param>
public sealed record OutgoingSms(string ToE164, string TemplateKey, string DltTemplateId, string SenderHeader, string Text);

/// <summary>What a provider said about one message.</summary>
/// <param name="Accepted">Whether the provider accepted it for delivery.</param>
/// <param name="Provider">The provider's name in settings.</param>
/// <param name="ProviderMessageId">The provider's id for the message, when accepted.</param>
/// <param name="Error">Why it was refused, without personal data.</param>
public sealed record SmsSendResult(bool Accepted, string Provider, string? ProviderMessageId = null, string? Error = null);

/// <summary>
/// Sends SMS through one DLT-registered provider (D-118). One adapter per provider; the provider is the
/// founder's choice (SGM-206 open question 1). Implementations never log the number in clear or the text,
/// because the text carries a one-time code (OI-038).
/// </summary>
public interface ISmsSender
{
    /// <summary>The provider's name, as used in settings and in <c>sms_messages.provider</c>.</summary>
    string Name { get; }

    /// <summary>Hands one message to the provider.</summary>
    /// <param name="message">The message.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<SmsSendResult> SendAsync(OutgoingSms message, CancellationToken cancellationToken = default);
}
