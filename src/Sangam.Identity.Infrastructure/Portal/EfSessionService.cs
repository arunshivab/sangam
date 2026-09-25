using Microsoft.EntityFrameworkCore;
using Sangam.Identity.Application.Abstractions;
using Sangam.Identity.Application.Portal;
using Sangam.Identity.Domain.Entities;
using Sangam.Identity.Domain.Enums;
using Sangam.Identity.Infrastructure.Persistence;

namespace Sangam.Identity.Infrastructure.Portal;

/// <summary><see cref="ISessionService"/> over <c>user_sessions</c>.</summary>
public sealed class EfSessionService : ISessionService
{
    /// <summary>How stale a session's last-seen time may be before it is written again.</summary>
    public static readonly TimeSpan TouchInterval = TimeSpan.FromMinutes(5);

    private readonly IDbContextFactory<SangamDbContext> _contextFactory;
    private readonly IClock _clock;

    /// <summary>Initialises the service.</summary>
    /// <param name="contextFactory">Context factory; sessions are written outside the caller's unit of work.</param>
    /// <param name="clock">Clock.</param>
    public EfSessionService(IDbContextFactory<SangamDbContext> contextFactory, IClock clock)
    {
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <inheritdoc />
    public async Task<Guid> StartAsync(Guid userId, SignInMode mode, Guid? appId, string? deviceLabel, string? ipAddress, string? userAgent, CancellationToken cancellationToken = default)
    {
        DateTimeOffset now = _clock.UtcNow;
        UserSession session = new()
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            AppId = appId,
            DeviceLabel = Trim(deviceLabel, 100),
            IpAddress = Trim(ipAddress, 45),
            UserAgent = Trim(userAgent, 500),
            SignInMode = mode,
            CreatedAt = now,
            LastSeenAt = now,
        };

        SangamDbContext db = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            db.UserSessions.Add(session);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        return session.Id;
    }

    /// <inheritdoc />
    public async Task<bool> TouchAsync(Guid sessionId, Guid userId, CancellationToken cancellationToken = default)
    {
        DateTimeOffset now = _clock.UtcNow;
        DateTimeOffset stale = now - TouchInterval;

        SangamDbContext db = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            bool live = await db.UserSessions
                .AnyAsync(s => s.Id == sessionId && s.UserId == userId && s.RevokedAt == null, cancellationToken)
                .ConfigureAwait(false);
            if (!live)
            {
                return false;
            }

            // One write per interval, not per request.
            await db.UserSessions
                .Where(s => s.Id == sessionId && s.LastSeenAt < stale)
                .ExecuteUpdateAsync(u => u.SetProperty(s => s.LastSeenAt, now), cancellationToken)
                .ConfigureAwait(false);
            return true;
        }
    }

    /// <inheritdoc />
    public async Task EndAsync(Guid sessionId, string reason, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reason);
        DateTimeOffset now = _clock.UtcNow;

        SangamDbContext db = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            await db.UserSessions
                .Where(s => s.Id == sessionId && s.RevokedAt == null)
                .ExecuteUpdateAsync(u => u.SetProperty(s => s.RevokedAt, now).SetProperty(s => s.RevokedReason, reason), cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static string? Trim(string? value, int max)
        => string.IsNullOrWhiteSpace(value) ? null : value.Length > max ? value[..max] : value;
}
