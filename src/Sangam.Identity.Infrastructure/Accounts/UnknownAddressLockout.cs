using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Sangam.Identity.Application.Abstractions;

namespace Sangam.Identity.Infrastructure.Accounts;

/// <summary>
/// D-L: wrong passwords for an address with no account are counted and "locked out" exactly like a real
/// account's, so the lockout message cannot be used to find out whether an address has an account. Kept in
/// memory only (keyed by a hash of the address); a restart forgets it, as it would a real lockout's effect on
/// an attacker who waits fifteen minutes.
/// </summary>
public sealed class UnknownAddressLockout
{
    private const int MaxEntries = 50_000;

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly IClock _clock;
    private readonly int _maxFailures;
    private readonly TimeSpan _lockout;

    /// <summary>Initialises the tracker with the same limits as accounts.</summary>
    /// <param name="clock">Clock.</param>
    /// <param name="maxFailures">Failures before the lockout (Identity's <c>MaxFailedAccessAttempts</c>).</param>
    /// <param name="lockout">How long the lockout lasts (Identity's <c>DefaultLockoutTimeSpan</c>).</param>
    public UnknownAddressLockout(IClock clock, int maxFailures, TimeSpan lockout)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _maxFailures = maxFailures;
        _lockout = lockout;
    }

    /// <summary>Whether the address is "locked out" now.</summary>
    /// <param name="email">The address typed.</param>
    public bool IsLockedOut(string email)
    {
        ArgumentNullException.ThrowIfNull(email);
        return _entries.TryGetValue(Key(email), out Entry? entry) && entry.LockedUntil > _clock.UtcNow;
    }

    /// <summary>Counts one wrong password; returns whether the address is now "locked out".</summary>
    /// <param name="email">The address typed.</param>
    public bool RecordFailure(string email)
    {
        ArgumentNullException.ThrowIfNull(email);
        DateTimeOffset now = _clock.UtcNow;
        if (_entries.Count >= MaxEntries)
        {
            Prune(now);
        }

        Entry entry = _entries.GetOrAdd(Key(email), _ => new Entry());
        lock (entry)
        {
            entry.LastSeen = now;
            entry.Failures++;
            if (entry.Failures >= _maxFailures)
            {
                entry.Failures = 0;
                entry.LockedUntil = now + _lockout;
                return true;
            }

            return false;
        }
    }

    private void Prune(DateTimeOffset now)
    {
        foreach ((string key, Entry entry) in _entries)
        {
            if (entry.LockedUntil < now && entry.LastSeen < now - _lockout)
            {
                _entries.TryRemove(key, out _);
            }
        }
    }

    private static string Key(string email)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(email.Trim().ToUpperInvariant())));

    private sealed class Entry
    {
        public int Failures { get; set; }

        public DateTimeOffset LockedUntil { get; set; }

        public DateTimeOffset LastSeen { get; set; }
    }
}
