using Microsoft.EntityFrameworkCore;
using Npgsql;
using Sangam.Identity.Infrastructure.Persistence;

namespace Sangam.Identity.Infrastructure.Maintenance;

/// <summary>
/// R7 (PR-33, OI-047): keeps a background round to one process at a time, however many hosts or replicas run it. The
/// round takes a PostgreSQL session advisory lock on its own connection; a process that cannot take it skips the round
/// (another is doing it). The lock goes with the connection, so a process that dies releases it at once.
/// </summary>
public static class ClusterLock
{
    /// <summary>The application-event dispatch, SCIM and webhook deliveries and membership expiry.</summary>
    public const long Provisioning = 0x5A6E_6761_5072_6F76;

    /// <summary>Back-channel logout deliveries.</summary>
    public const long BackChannelLogout = 0x5A6E_6761_4C6F_676F;

    /// <summary>Account purge and retention sweeps.</summary>
    public const long Maintenance = 0x5A6E_6761_4D61_696E;

    /// <summary>Alert evaluation and metric pruning.</summary>
    public const long Alerts = 0x5A6E_6761_416C_7274;

    /// <summary>Runs <paramref name="round"/> if no other process holds <paramref name="key"/>; returns whether it ran.</summary>
    /// <param name="db">Any context on the Sangam database (only its connection string is used).</param>
    /// <param name="key">The lock, one of this class's constants.</param>
    /// <param name="round">The work.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public static async Task<bool> TryRunAsync(SangamDbContext db, long key, Func<CancellationToken, Task> round, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(round);
        string connectionString = db.Database.GetConnectionString()
            ?? throw new InvalidOperationException("The Sangam database has no connection string.");
        NpgsqlConnection connection = new(connectionString);
        await using (connection.ConfigureAwait(false))
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            NpgsqlCommand take = new("SELECT pg_try_advisory_lock(@key)", connection);
            await using (take.ConfigureAwait(false))
            {
                take.Parameters.AddWithValue("key", key);
                if (await take.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not true)
                {
                    return false;
                }
            }

            try
            {
                await round(cancellationToken).ConfigureAwait(false);
                return true;
            }
            finally
            {
                NpgsqlCommand release = new("SELECT pg_advisory_unlock(@key)", connection);
                await using (release.ConfigureAwait(false))
                {
                    release.Parameters.AddWithValue("key", key);
                    await release.ExecuteScalarAsync(CancellationToken.None).ConfigureAwait(false);
                }
            }
        }
    }
}
