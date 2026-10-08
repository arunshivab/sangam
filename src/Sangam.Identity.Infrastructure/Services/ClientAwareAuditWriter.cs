using Sangam.Identity.Application.Abstractions;

namespace Sangam.Identity.Infrastructure.Services;

/// <summary>
/// Scoped <see cref="IAuditWriter"/> that fills a missing IP address and user agent from the
/// current <see cref="IClientContext"/>, so events from the consoles, the portal and the API carry
/// client details (OI-039). Background jobs have no client, and their entries stay without one.
/// </summary>
public sealed class ClientAwareAuditWriter : IAuditWriter
{
    private readonly EfAuditWriter _inner;
    private readonly IClientContext _client;

    /// <summary>Initialises the writer.</summary>
    /// <param name="inner">The chained database writer.</param>
    /// <param name="client">The current client.</param>
    public ClientAwareAuditWriter(EfAuditWriter inner, IClientContext client)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    /// <inheritdoc />
    public Task WriteAsync(AuditEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        AuditEntry filled = entry with
        {
            IpAddress = entry.IpAddress ?? _client.IpAddress,
            UserAgent = entry.UserAgent ?? _client.UserAgent,
        };
        return _inner.WriteAsync(filled, cancellationToken);
    }
}
