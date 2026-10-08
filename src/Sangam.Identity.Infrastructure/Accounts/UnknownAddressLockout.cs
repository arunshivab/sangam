using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Sangam.Identity.Application.Abstractions;
using Sangam.Identity.Infrastructure.Persistence;

namespace Sangam.Identity.Infrastructure.Accounts;

/// <summary>
/// D-L: wrong passwords for an address with no account are counted and "locked out" exactly like a real account's, so
/// the lockout message cannot be used to find out whether an address has an account. R7 (PR-33): the count is kept in
/// the database (keyed by a hash of the address), like a real account's, so it stays the same however many
/// identity-server replicas share the work — counted per process, two replicas would have allowed ten tries where an
/// account allows five, which would itself tell the two apart.
/// </summary>
public sealed class UnknownAddressLockout
{
    private readonly IDbContextFactory<SangamDbContext> _contexts;
    private readonly IClock _clock;
    private readonly int _maxFailures;
    private readonly TimeSpan _lockout;

    /// <summary>Initialises the tracker with the same limits as accounts.</summary>
    /// <param name="contexts">Database.</param>
    /// <param name="clock">Clock.</param>
    /// <param name="maxFailures">Failures before the lockout (Identity's <c>MaxFailedAccessAttempts</c>).</param>
    /// <param name="lockout">How long the lockout lasts (Identity's <c>DefaultLockoutTimeSpan</c>).</param>
    public UnknownAddressLockout(IDbContextFactory<SangamDbContext> contexts, IClock clock, int maxFailures, TimeSpan lockout)
    {
        _contexts = contexts ?? throw new ArgumentNullException(nameof(contexts));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _maxFailures = maxFailures;
        _lockout = lockout;
    }

    /// <summary>Whether the address is "locked out" now.</summary>
    /// <param name="email">The address typed.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public async Task<bool> IsLockedOutAsync(string email, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(email);
        string key = Key(email);
        DateTimeOffset now = _clock.UtcNow;
        SangamDbContext db = await _contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            return await db.UnknownAddressAttempts.AsNoTracking().AnyAsync(a => a.Key == key && a.LockedUntil > now, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Counts one wrong password in one statement; returns whether the address is now "locked out".</summary>
    /// <param name="email">The address typed.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public async Task<bool> RecordFailureAsync(string email, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(email);
        DateTimeOffset now = _clock.UtcNow;
        SangamDbContext db = await _contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            // A count left idle for a lockout period starts again, as it did when the count lived in memory.
            List<DateTimeOffset?> locked = await db.Database.SqlQueryRaw<DateTimeOffset?>(
                "INSERT INTO unknown_address_attempts (key, failures, locked_until, last_seen) " +
                "VALUES (@key, CASE WHEN @max <= 1 THEN 0 ELSE 1 END, CASE WHEN @max <= 1 THEN @until END, @now) " +
                "ON CONFLICT (key) DO UPDATE SET " +
                "failures = CASE WHEN (CASE WHEN unknown_address_attempts.last_seen < @stale THEN 0 ELSE unknown_address_attempts.failures END) + 1 >= @max " +
                "THEN 0 ELSE (CASE WHEN unknown_address_attempts.last_seen < @stale THEN 0 ELSE unknown_address_attempts.failures END) + 1 END, " +
                "locked_until = CASE WHEN (CASE WHEN unknown_address_attempts.last_seen < @stale THEN 0 ELSE unknown_address_attempts.failures END) + 1 >= @max " +
                "THEN @until ELSE unknown_address_attempts.locked_until END, " +
                "last_seen = @now " +
                "RETURNING locked_until AS \"Value\"",
                new NpgsqlParameter("key", Key(email)),
                new NpgsqlParameter("max", _maxFailures),
                new NpgsqlParameter("until", now + _lockout),
                new NpgsqlParameter("stale", now - _lockout),
                new NpgsqlParameter("now", now))
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            return locked.Count == 1 && locked[0] > now;
        }
    }

    /// <summary>Forgets counts idle for a day; for the maintenance sweep.</summary>
    /// <param name="db">Database.</param>
    /// <param name="now">The time now.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public static Task<int> PruneAsync(SangamDbContext db, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        DateTimeOffset cutoff = now - TimeSpan.FromDays(1);
        return db.UnknownAddressAttempts.Where(a => a.LastSeen < cutoff && (a.LockedUntil == null || a.LockedUntil < now)).ExecuteDeleteAsync(cancellationToken);
    }

    private static string Key(string email)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(email.Trim().ToUpperInvariant())));
}
