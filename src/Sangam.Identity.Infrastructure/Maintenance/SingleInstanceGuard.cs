using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Sangam.Identity.Infrastructure.Maintenance;

/// <summary>
/// rc.16 (ASVS V1.11.2, founder decision of 26 Oct 2026): exactly one identity server runs against a database. The
/// sign-in rate limits are kept in each process's memory, which is right for one process and wrong for two (each would
/// allow the full limit); everything else a sign-in depends on is in the database. Rather than move them into the database, the identity
/// server holds a PostgreSQL advisory lock for as long as it runs; a second one finds the lock taken and refuses to start.
/// <para>
/// The lock lives on a connection of its own. If that connection is lost (the database restarted), the guard takes the
/// lock again; if another identity server took it meanwhile, this one stops itself. On by default outside Development
/// and Testing; <c>Sangam:SingleInstance:Enabled = false</c> turns it off for a deployment that shares its limits another
/// way (deploy/kubernetes/README.md).
/// </para>
/// </summary>
public sealed partial class SingleInstanceGuard : IHostedLifecycleService, IAsyncDisposable
{
    /// <summary>The setting that turns the guard on or off.</summary>
    public const string EnabledKey = "Sangam:SingleInstance:Enabled";

    /// <summary>The advisory lock the identity server holds while it runs.</summary>
    public const long IdentityServer = 0x5A6E_6761_4964_656E;

    private readonly string? _connectionString;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<SingleInstanceGuard> _logger;
    private readonly bool _enabled;
    private readonly TimeSpan _interval;
    private readonly CancellationTokenSource _stopping = new();
    private NpgsqlConnection? _connection;
    private Task? _watch;
    private int _disposed;
    private int _backend;

    /// <summary>Initialises the guard.</summary>
    /// <param name="configuration">Reads the connection string, <see cref="EnabledKey"/> and <c>Sangam:SingleInstance:CheckInterval</c>.</param>
    /// <param name="environment">Host environment.</param>
    /// <param name="lifetime">Stops the host when another identity server holds the lock.</param>
    /// <param name="logger">Logger.</param>
    public SingleInstanceGuard(IConfiguration configuration, IHostEnvironment environment, IHostApplicationLifetime lifetime, ILogger<SingleInstanceGuard> logger)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);
        _lifetime = lifetime ?? throw new ArgumentNullException(nameof(lifetime));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        bool local = environment.EnvironmentName is "Development" or "Testing";
        _enabled = configuration.GetValue(EnabledKey, !local);
        _interval = configuration.GetValue("Sangam:SingleInstance:CheckInterval", TimeSpan.FromSeconds(30));
        string? connectionString = configuration.GetConnectionString("Sangam");

        // A connection of its own, outside the pool: closing it ends the session, and with it the lock. A pooled
        // connection would go back to the pool still holding the lock.
        _connectionString = string.IsNullOrWhiteSpace(connectionString) ? null : new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString;
    }

    /// <summary>Whether this process holds the lock.</summary>
    public bool Holding => _connection is { State: System.Data.ConnectionState.Open };

    /// <inheritdoc />
    public async Task StartingAsync(CancellationToken cancellationToken)
    {
        if (!_enabled)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(_connectionString))
        {
            throw new InvalidOperationException("The single-instance guard needs ConnectionStrings:Sangam.");
        }

        if (!await TryTakeAsync(cancellationToken).ConfigureAwait(false))
        {
            LogRefused();
            throw new InvalidOperationException(
                "Another identity server is already running against this database. Sangam runs exactly one (ASVS V1.11.2): stop the other first. "
                + $"To run several deliberately, set {EnabledKey} = false only where the sign-in limits are shared.");
        }

        LogHolding();
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public Task StartedAsync(CancellationToken cancellationToken)
    {
        if (_enabled)
        {
            _watch = WatchAsync(_stopping.Token);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        // The host may dispose the guard before it stops it (a test host torn down early); stopping then has nothing to do.
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        await _stopping.CancelAsync().ConfigureAwait(false);
        if (_watch is not null)
        {
            try
            {
                await _watch.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        await ReleaseAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _stopping.CancelAsync().ConfigureAwait(false);
        if (_watch is not null)
        {
            try
            {
                await _watch.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        await ReleaseAsync().ConfigureAwait(false);
        _stopping.Dispose();
    }

    /// <summary>Takes the lock on a connection of its own; false when another process holds it.</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    public async Task<bool> TryTakeAsync(CancellationToken cancellationToken = default)
    {
        await ReleaseAsync().ConfigureAwait(false);
        NpgsqlConnection? connection = null;
        try
        {
            connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            NpgsqlCommand take = new("SELECT pg_try_advisory_lock(@key)", connection);
            await using (take.ConfigureAwait(false))
            {
                take.Parameters.AddWithValue("key", IdentityServer);
                if (await take.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not true)
                {
                    return false;
                }
            }

            // The connection now holds the lock; it is kept (and closed by ReleaseAsync), not disposed here.
            _connection = connection;
            _backend = connection.ProcessID;
            connection = null;
            return true;
        }
        finally
        {
            connection?.Dispose();
        }
    }

    private async Task WatchAsync(CancellationToken cancellationToken)
    {
        using PeriodicTimer timer = new(_interval);
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await CheckOnceAsync(cancellationToken).ConfigureAwait(false))
            {
                return;
            }
        }
    }

    /// <summary>
    /// One round of the watch: still holding, or taken again, is true; another identity server holding the lock stops
    /// this one and is false.
    /// </summary>
    /// <param name="cancellationToken">Cancellation.</param>
    internal async Task<bool> CheckOnceAsync(CancellationToken cancellationToken = default)
    {
        if (await StillHoldingAsync(cancellationToken).ConfigureAwait(false))
        {
            return true;
        }

        try
        {
            if (await TryTakeAsync(cancellationToken).ConfigureAwait(false))
            {
                LogRetaken();
                return true;
            }

            // The lock may still be held by this process's own old session: a connection that broke without the
            // database noticing yet. That one is ended (a role may end its own sessions) and the lock taken again.
            // Only a holder that is not this process's old session is another identity server.
            int? holder = await HolderAsync(cancellationToken).ConfigureAwait(false);
            if (holder is null || holder == _backend)
            {
                if (holder == _backend)
                {
                    await EndOldSessionAsync(_backend, cancellationToken).ConfigureAwait(false);
                }

                if (await TryTakeAsync(cancellationToken).ConfigureAwait(false))
                {
                    LogRetaken();
                    return true;
                }
            }

            LogLost();
            _lifetime.StopApplication();
            return false;
        }
        catch (NpgsqlException exception)
        {
            // The database is still away; nobody else can take the lock without it either. Try again next time.
            LogDatabaseAway(exception);
            return true;
        }
    }

    /// <summary>The database session this guard's lock was taken on (tests).</summary>
    internal int Backend => _backend;

    /// <summary>Loses the lock's connection without closing it, as a broken network would (tests).</summary>
    internal void LoseConnectionForTests() => _connection = null;

    /// <summary>The database session that holds the lock now, if any.</summary>
    internal async Task<int?> HolderAsync(CancellationToken cancellationToken)
    {
        NpgsqlConnection connection = new(_connectionString);
        await using (connection.ConfigureAwait(false))
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            NpgsqlCommand query = new(
                "SELECT pid FROM pg_locks WHERE locktype = 'advisory' AND granted AND database = (SELECT oid FROM pg_database WHERE datname = current_database()) AND classid = @high AND objid = @low AND objsubid = 1 LIMIT 1",
                connection);
            await using (query.ConfigureAwait(false))
            {
                // A bigint advisory key is kept as its high and low 32 bits.
                query.Parameters.AddWithValue("high", NpgsqlTypes.NpgsqlDbType.Oid, (uint)((ulong)IdentityServer >> 32));
                query.Parameters.AddWithValue("low", NpgsqlTypes.NpgsqlDbType.Oid, (uint)((ulong)IdentityServer & 0xFFFF_FFFF));
                return await query.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is int pid ? pid : null;
            }
        }
    }

    private async Task EndOldSessionAsync(int backend, CancellationToken cancellationToken)
    {
        NpgsqlConnection connection = new(_connectionString);
        await using (connection.ConfigureAwait(false))
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            NpgsqlCommand end = new("SELECT pg_terminate_backend(@pid, 5000)", connection);
            await using (end.ConfigureAwait(false))
            {
                end.Parameters.AddWithValue("pid", backend);
                await end.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        LogEndedOldSession(backend);
    }

    private async Task<bool> StillHoldingAsync(CancellationToken cancellationToken)
    {
        if (_connection is null)
        {
            return false;
        }

        try
        {
            NpgsqlCommand ping = new("SELECT 1", _connection);
            await using (ping.ConfigureAwait(false))
            {
                await ping.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            }

            return true;
        }
        catch (NpgsqlException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private async Task ReleaseAsync()
    {
        NpgsqlConnection? connection = Interlocked.Exchange(ref _connection, null);
        if (connection is not null)
        {
            // Closing the connection releases a session advisory lock.
            await connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    [LoggerMessage(EventId = 1260, Level = LogLevel.Critical, Message = "Another identity server already runs against this database; this one refuses to start (Sangam runs exactly one).")]
    private partial void LogRefused();

    [LoggerMessage(EventId = 1261, Level = LogLevel.Information, Message = "This is the one identity server for its database (single-instance lock held).")]
    private partial void LogHolding();

    [LoggerMessage(EventId = 1262, Level = LogLevel.Warning, Message = "The single-instance lock's connection was lost and has been taken again.")]
    private partial void LogRetaken();

    [LoggerMessage(EventId = 1263, Level = LogLevel.Critical, Message = "Another identity server took the single-instance lock while this one's connection was lost; this one is stopping.")]
    private partial void LogLost();

    [LoggerMessage(EventId = 1264, Level = LogLevel.Warning, Message = "The database cannot be reached to take the single-instance lock again; trying again shortly.")]
    private partial void LogDatabaseAway(Exception exception);

    [LoggerMessage(EventId = 1265, Level = LogLevel.Warning, Message = "This identity server's old database session {Backend} still held the single-instance lock; it was ended.")]
    private partial void LogEndedOldSession(int backend);
}
