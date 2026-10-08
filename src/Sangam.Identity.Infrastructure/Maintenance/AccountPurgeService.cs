using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Sangam.Identity.Application.Abstractions;
using Sangam.Identity.Domain;
using Sangam.Identity.Domain.Entities;
using Sangam.Identity.Domain.Enums;
using Sangam.Identity.Infrastructure.Audit;
using Sangam.Identity.Infrastructure.Persistence;

namespace Sangam.Identity.Infrastructure.Maintenance;

/// <summary>
/// Hourly housekeeping: hard-deletes accounts whose 30-day grace period has passed, prunes
/// session rows that have been revoked for more than 90 days, and runs the audit archive (D-A).
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
    private readonly AuditArchiver _archiver;

    /// <summary>Initialises the service.</summary>
    /// <param name="scopeFactory">Scope factory for the scoped database context.</param>
    /// <param name="clock">Clock.</param>
    /// <param name="configuration">Reads <c>Sangam:Maintenance:Enabled</c> and <c>Sangam:Maintenance:Interval</c>.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="archiver">The audit archive (D-A).</param>
    public AccountPurgeService(IServiceScopeFactory scopeFactory, IClock clock, IConfiguration configuration, ILogger<AccountPurgeService> logger, AuditArchiver archiver)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _enabled = configuration.GetValue("Sangam:Maintenance:Enabled", true);
        _interval = configuration.GetValue("Sangam:Maintenance:Interval", TimeSpan.FromHours(1));
        _archiver = archiver ?? throw new ArgumentNullException(nameof(archiver));
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

                await _archiver.RunOnceAsync(stoppingToken).ConfigureAwait(false);
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
        (int Accounts, int Sessions) result = (0, 0);

        // R7 (PR-33, OI-047): every host runs this service; one at a time sweeps, so no account is purged twice.
        await ClusterLock.TryRunAsync(db, ClusterLock.Maintenance, async ct => result = await SweepAsync(db, audit, ct).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
        return result;
    }

    private async Task<(int Accounts, int Sessions)> SweepAsync(SangamDbContext db, IAuditWriter audit, CancellationToken cancellationToken)
    {
        DateTimeOffset now = _clock.UtcNow;

        List<SangamUser> due = await db.Users
            .Where(u => u.PurgeAfter != null && u.PurgeAfter <= now && u.HoldPlacedAt == null && u.Status != UserStatus.DeletedHard)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        foreach (SangamUser user in due)
        {
            await Provisioning.AppEventLog.AddAsync(db, AppEventTypes.UserDeactivated, null, user.Id, null, new Dictionary<string, object?> { ["reason"] = "deleted" }, now, cancellationToken).ConfigureAwait(false);
            Pseudonymise(user, now);
            await audit.WriteAsync(
                new AuditEntry(AuditActions.UserAccountDeletionComplete, AuditActorType.System, TargetType: "user", TargetId: user.Id),
                cancellationToken).ConfigureAwait(false);
        }

        if (due.Count > 0)
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            // PR-25/26: what applications kept about the person, and their verified identity (with the DigiLocker id's
            // hash, so the same person can verify a new account later), go with the account.
            List<Guid> ids = [.. due.Select(u => u.Id)];
            await db.UserAttributeValues.Where(v => ids.Contains(v.UserId)).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            await db.IdentityVerifications.Where(v => ids.Contains(v.UserId)).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        }

        DateTimeOffset cutoff = now - SessionRetention;
        int sessions = await db.UserSessions
            .Where(s => s.RevokedAt != null && s.RevokedAt < cutoff)
            .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);

        // PR-23/24: events are kept 30 days after they were handed out, finished deliveries 90 days (the delivery log).
        DateTimeOffset events = now - TimeSpan.FromDays(30);
        await db.AppEvents.Where(e => e.DispatchedAt != null && e.DispatchedAt < events).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        DateTimeOffset log = now - TimeSpan.FromDays(90);
        await db.ScimDeliveries.Where(d => d.CompletedAt != null && d.CompletedAt < log).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await db.WebhookDeliveries.Where(d => d.CompletedAt != null && d.CompletedAt < log).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);

        // R7: counts of wrong passwords for unknown addresses are forgotten after a day.
        await Accounts.UnknownAddressLockout.PruneAsync(db, now, cancellationToken).ConfigureAwait(false);

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
        user.IdentityVerifiedAt = null;
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
