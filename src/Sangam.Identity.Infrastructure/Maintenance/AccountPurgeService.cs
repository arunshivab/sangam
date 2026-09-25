using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Sangam.Identity.Application.Abstractions;
using Sangam.Identity.Domain;
using Sangam.Identity.Domain.Entities;
using Sangam.Identity.Domain.Enums;
using Sangam.Identity.Infrastructure.Persistence;

namespace Sangam.Identity.Infrastructure.Maintenance;

/// <summary>
/// Hourly housekeeping: hard-deletes accounts whose 30-day grace period has passed, and prunes
/// session rows that have been revoked for more than 90 days.
/// <para>
/// A hard delete pseudonymises the user row — name, email, mobile and password hash are
/// destroyed and the email is replaced with an unusable placeholder — but the row and the audit
/// events survive, so the log stays coherent. An account under an operator hold is skipped.
/// </para>
/// </summary>
public sealed partial class AccountPurgeService : BackgroundService
{
    /// <summary>How long a revoked session row is kept before it is deleted.</summary>
    public static readonly TimeSpan SessionRetention = TimeSpan.FromDays(90);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IClock _clock;
    private readonly ILogger<AccountPurgeService> _logger;
    private readonly TimeSpan _interval;
    private readonly bool _enabled;

    /// <summary>Initialises the service.</summary>
    /// <param name="scopeFactory">Scope factory for the scoped database context.</param>
    /// <param name="clock">Clock.</param>
    /// <param name="configuration">Reads <c>Sangam:Maintenance:Enabled</c> and <c>Sangam:Maintenance:Interval</c>.</param>
    /// <param name="logger">Logger.</param>
    public AccountPurgeService(IServiceScopeFactory scopeFactory, IClock clock, IConfiguration configuration, ILogger<AccountPurgeService> logger)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _enabled = configuration.GetValue("Sangam:Maintenance:Enabled", true);
        _interval = configuration.GetValue("Sangam:Maintenance:Interval", TimeSpan.FromHours(1));
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_enabled)
        {
            LogDisabled();
            return;
        }

        using PeriodicTimer timer = new(_interval);
        do
        {
            try
            {
                (int purged, int sessions) = await RunOnceAsync(stoppingToken).ConfigureAwait(false);
                if (purged > 0 || sessions > 0)
                {
                    LogSwept(purged, sessions);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
#pragma warning disable CA1031 // A background sweep must never take the host down.
            catch (Exception exception)
            {
                LogFailed(exception);
            }
#pragma warning restore CA1031
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    /// <summary>Runs one sweep. Exposed so tests can drive it without waiting for the timer.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>How many accounts were purged and how many session rows deleted.</returns>
    public async Task<(int Accounts, int Sessions)> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        using IServiceScope scope = _scopeFactory.CreateScope();
        SangamDbContext db = scope.ServiceProvider.GetRequiredService<SangamDbContext>();
        IAuditWriter audit = scope.ServiceProvider.GetRequiredService<IAuditWriter>();
        DateTimeOffset now = _clock.UtcNow;

        List<SangamUser> due = await db.Users
            .Where(u => u.PurgeAfter != null && u.PurgeAfter <= now && u.HoldPlacedAt == null && u.Status != UserStatus.DeletedHard)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        foreach (SangamUser user in due)
        {
            Pseudonymise(user, now);
            await audit.WriteAsync(
                new AuditEntry(AuditActions.UserAccountDeletionComplete, AuditActorType.System, TargetType: "user", TargetId: user.Id),
                cancellationToken).ConfigureAwait(false);
        }

        if (due.Count > 0)
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        DateTimeOffset cutoff = now - SessionRetention;
        int sessions = await db.UserSessions
            .Where(s => s.RevokedAt != null && s.RevokedAt < cutoff)
            .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);

        return (due.Count, sessions);
    }

    /// <summary>
    /// Destroys the personal data on a user row while keeping the row itself, so foreign keys and
    /// the audit trail stay intact.
    /// </summary>
    /// <param name="user">The user to pseudonymise.</param>
    /// <param name="now">Current time.</param>
    public static void Pseudonymise(SangamUser user, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(user);
        string placeholder = $"deleted-{user.Id:N}@deleted.sangamid.in";

        user.FirstName = "Deleted";
        user.LastName = "account";
        user.Email = placeholder;
        user.NormalizedEmail = placeholder.ToUpperInvariant();
        user.UserName = placeholder;
        user.NormalizedUserName = placeholder.ToUpperInvariant();
        user.EmailConfirmed = false;
        user.PhoneNumber = null;
        user.PhoneNumberConfirmed = false;
        user.PasswordHash = null;
        user.SecurityStamp = Guid.NewGuid().ToString("N");
        user.DateOfBirth = default;
        user.Gender = Gender.PreferNotToSay;
        user.Status = UserStatus.DeletedHard;
        user.PurgeAfter = null;
        user.DeletedAt ??= now;
        user.UpdatedAt = now;
    }

    [LoggerMessage(EventId = 1200, Level = LogLevel.Information, Message = "Maintenance sweep: purged {Accounts} account(s), removed {Sessions} old session row(s).")]
    private partial void LogSwept(int accounts, int sessions);

    [LoggerMessage(EventId = 1201, Level = LogLevel.Information, Message = "Maintenance sweep is disabled (Sangam:Maintenance:Enabled = false).")]
    private partial void LogDisabled();

    [LoggerMessage(EventId = 1202, Level = LogLevel.Error, Message = "The maintenance sweep failed; it will run again at the next interval.")]
    private partial void LogFailed(Exception exception);
}
