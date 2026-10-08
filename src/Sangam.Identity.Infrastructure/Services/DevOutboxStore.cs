using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Sangam.Identity.Domain.Entities;
using Sangam.Identity.Infrastructure.Monitoring;
using Sangam.Identity.Infrastructure.Persistence;

namespace Sangam.Identity.Infrastructure.Services;

/// <summary>
/// Development only (V-11): the four hosts' outboxes share one table, so an e-mail the operator console raises —
/// such as the reset alert with its cancel link — shows on the identity server's <c>/dev/outbox</c> with the rest.
/// Registered only when the development outbox is on (<c>Sangam:Email:UseOutbox</c>, which the start-up guard
/// refuses outside Development and Testing) and <c>Sangam:Email:SharedOutbox</c> is set. Writes never fail a send.
/// </summary>
public sealed partial class DevOutboxStore
{
    /// <summary>Configuration key that switches the shared outbox on.</summary>
    public const string SharedKey = "Sangam:Email:SharedOutbox";

    /// <summary>How many rows are kept.</summary>
    public const int Keep = 500;

    private readonly IDbContextFactory<SangamDbContext> _contexts;
    private readonly ILogger<DevOutboxStore> _logger;

    /// <summary>Initialises the store.</summary>
    /// <param name="contexts">Database contexts.</param>
    /// <param name="configuration">Configuration (the host's name).</param>
    /// <param name="logger">Logger.</param>
    public DevOutboxStore(IDbContextFactory<SangamDbContext> contexts, IConfiguration configuration, ILogger<DevOutboxStore> logger)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        _contexts = contexts ?? throw new ArgumentNullException(nameof(contexts));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        Host = MetricsRecorder.HostNameFrom(configuration);
    }

    /// <summary>This host's name on its rows.</summary>
    public string Host { get; }

    /// <summary>Keeps one message; failures are logged, never thrown.</summary>
    /// <param name="kind"><c>email</c> or <c>sms</c>.</param>
    /// <param name="recipient">Address or number.</param>
    /// <param name="subject">Subject, or template and sender.</param>
    /// <param name="body">Text body.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task RecordAsync(string kind, string recipient, string subject, string body, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(kind);
        ArgumentNullException.ThrowIfNull(recipient);
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(body);
        try
        {
            SangamDbContext db = await _contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            await using (db.ConfigureAwait(false))
            {
                db.DevOutbox.Add(new DevOutboxMessage
                {
                    SentAt = DateTimeOffset.UtcNow,
                    Host = Host,
                    Kind = kind,
                    Recipient = Cut(recipient, 320),
                    Subject = Cut(subject, 500),
                    Body = body,
                });
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                long newest = await db.DevOutbox.MaxAsync(m => m.Id, cancellationToken).ConfigureAwait(false);
                await db.DevOutbox.Where(m => m.Id <= newest - Keep).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException or Npgsql.NpgsqlException)
        {
            LogFailed(ex);
        }
    }

    /// <summary>The newest messages of one kind from every host.</summary>
    /// <param name="kind"><c>email</c> or <c>sms</c>.</param>
    /// <param name="count">How many.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<IReadOnlyList<DevOutboxMessage>> RecentAsync(string kind, int count, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(kind);
        SangamDbContext db = await _contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            return await db.DevOutbox.AsNoTracking().Where(m => m.Kind == kind).OrderByDescending(m => m.Id).Take(count).ToListAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static string Cut(string value, int length) => value.Length > length ? value[..length] : value;

    [LoggerMessage(EventId = 1003, Level = LogLevel.Warning, Message = "The shared development outbox could not keep a message")]
    private partial void LogFailed(Exception exception);
}
