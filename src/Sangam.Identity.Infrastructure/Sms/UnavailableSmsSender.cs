using Sangam.Identity.Application.Abstractions;

namespace Sangam.Identity.Infrastructure.Sms;

/// <summary>Used when no provider adapter is configured: every message is refused, and nothing is logged.</summary>
public sealed class UnavailableSmsSender : ISmsSender
{
    /// <inheritdoc />
    public string Name => "none";

    /// <inheritdoc />
    public Task<SmsSendResult> SendAsync(OutgoingSms message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        return Task.FromResult(new SmsSendResult(false, Name, Error: "no SMS provider is configured"));
    }
}
