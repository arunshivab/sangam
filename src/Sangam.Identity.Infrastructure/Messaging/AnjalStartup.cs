using Microsoft.Extensions.Hosting;

namespace Sangam.Identity.Infrastructure.Messaging;

/// <summary>
/// rc.17: makes the Anjal client when the host starts, so a host that signs its requests logs its key id at start-up
/// (the go-live checklist compares it with the id Anjal shows) instead of at its first message.
/// </summary>
internal sealed class AnjalStartup : IHostedService
{
    /// <summary>Initialises the start-up step.</summary>
    /// <param name="client">The Anjal client; asking for it is the point.</param>
    public AnjalStartup(AnjalClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
