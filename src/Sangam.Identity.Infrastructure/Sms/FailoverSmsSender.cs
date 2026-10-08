using Sangam.Identity.Application.Abstractions;

namespace Sangam.Identity.Infrastructure.Sms;

/// <summary>
/// Tries each provider in order until one accepts the message (SGM-206 §4, optional failover). A provider
/// that throws is treated as a refusal, so one provider's outage never stops a sign-in when another is set.
/// </summary>
public sealed class FailoverSmsSender : ISmsSender
{
    private readonly IReadOnlyList<ISmsSender> _providers;

    /// <summary>Initialises the sender.</summary>
    /// <param name="providers">Providers, primary first.</param>
    public FailoverSmsSender(IReadOnlyList<ISmsSender> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);
        if (providers.Count == 0)
        {
            throw new ArgumentException("At least one provider is needed.", nameof(providers));
        }

        _providers = providers;
    }

    /// <inheritdoc />
    public string Name => _providers[0].Name;

    /// <inheritdoc />
    public async Task<SmsSendResult> SendAsync(OutgoingSms message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        SmsSendResult last = new(false, Name, Error: "not attempted");
        foreach (ISmsSender provider in _providers)
        {
            try
            {
                last = await provider.SendAsync(message, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or TimeoutException or IOException or InvalidOperationException)
            {
                last = new SmsSendResult(false, provider.Name, Error: ex.GetType().Name);
            }

            if (last.Accepted)
            {
                return last;
            }
        }

        return last;
    }
}
