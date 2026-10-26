using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenIddict.Abstractions;
using Sangam.Identity.Application.Abstractions;
using Sangam.Identity.Domain;
using Sangam.Identity.Domain.Entities;
using Sangam.Identity.Domain.Enums;
using Sangam.Identity.Infrastructure.Audit;
using Sangam.Identity.Infrastructure.Persistence;

namespace Sangam.Identity.Infrastructure.Maintenance;

/// <summary>
/// Hourly housekeeping: hard-deletes accounts whose 30-day grace period has passed, prunes
/// session rows that have been revoked for more than 90 days, deletes short-lived records past their retention
/// (rc.6, <see cref="RetentionSweep"/>), deletes finished sign-in tokens and authorizations (rc.16), reminds applications' owners about old client secrets (rc.15,
/// <see cref="ClientSecretReminders"/>), and runs the audit archive (D-A).
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

    /// <summary>
    /// rc.16 (founder decision of 26 Oct 2026): a sign-in token or authorization that is no longer usable (revoked,
    /// redeemed, expired, or whose authorization ended) is deleted once it was created this long ago. Refresh tokens last
    /// 14 days, so a few weeks of finished tokens stay for looking into a problem; sign-ins themselves are in the audit log.
    /// </summary>
    public static readonly TimeSpan TokenRetention = TimeSpan.FromDays(30);

    /// <summary>The longest chain of merges <see cref="ForgetMergesAsync"/> follows (as many as "merged into" follows).</summary>
    private const int MaxMergeHops = 10;

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
        InactivityService inactivity = scope.ServiceProvider.GetRequiredService<InactivityService>();
        ClientSecretReminders secrets = scope.ServiceProvider.GetRequiredService<ClientSecretReminders>();
        IOpenIddictTokenManager tokens = scope.ServiceProvider.GetRequiredService<IOpenIddictTokenManager>();
        IOpenIddictAuthorizationManager authorizations = scope.ServiceProvider.GetRequiredService<IOpenIddictAuthorizationManager>();
        (int Accounts, int Sessions) result = (0, 0);

        // R7 (PR-33, OI-047): every host runs this service; one at a time sweeps, so no account is purged twice.
        // rc.6: inactive connections and accounts first, so an account scheduled now is purged in the same sweep.
        await ClusterLock.TryRunAsync(
            db,
            ClusterLock.Maintenance,
            async ct =>
            {
                await inactivity.RunAsync(ct).ConfigureAwait(false);

                // rc.15 (ASVS V2.10.1): owners are told when a client secret is old or ends soon. A failure here never
                // stops the purge.
                try
                {
                    await secrets.RunAsync(ct).ConfigureAwait(false);
                }
#pragma warning disable CA1031 // Reminders must never hold up the purge.
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    LogRemindersFailed(exception);
                }
#pragma warning restore CA1031
                result = await SweepAsync(db, audit, ct).ConfigureAwait(false);
                await PruneTokensAsync(tokens, authorizations, _clock.UtcNow, ct).ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);
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
            await ForgetMergesAsync(db, ids, cancellationToken).ConfigureAwait(false);
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

        // rc.6 (SGM-910 section 6): codes, finished requests, the SMS log and closed grievances.
        await RetentionSweep.RunAsync(db, now, cancellationToken).ConfigureAwait(false);

        // R7: counts of wrong passwords for unknown addresses are forgotten after a day.
        await Accounts.UnknownAddressLockout.PruneAsync(db, now, cancellationToken).ConfigureAwait(false);

        return (due.Count, sessions);
    }

    /// <summary>
    /// rc.16: deletes the sign-in tokens, then the authorizations, that can no longer be used and were created more than
    /// <see cref="TokenRetention"/> ago. OpenIddict's own prune keeps anything still valid, whatever its age.
    /// </summary>
    /// <param name="tokens">OpenIddict's token manager.</param>
    /// <param name="authorizations">OpenIddict's authorization manager.</param>
    /// <param name="now">Current time.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>How many tokens and authorizations were deleted.</returns>
    public static async Task<(long Tokens, long Authorizations)> PruneTokensAsync(IOpenIddictTokenManager tokens, IOpenIddictAuthorizationManager authorizations, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(authorizations);
        DateTimeOffset threshold = now - TokenRetention;

        // Tokens first: an authorization with a token still attached is never pruned.
        long removedTokens = await tokens.PruneAsync(threshold, cancellationToken).ConfigureAwait(false);
        long removedAuthorizations = await authorizations.PruneAsync(threshold, cancellationToken).ConfigureAwait(false);
        return (removedTokens, removedAuthorizations);
    }

    /// <summary>
    /// rc.8 (SGM-910): when an account that others were merged into is deleted, the applied merge records that point at it
    /// go too. They were kept only so a closed id could answer "merged into" this account; once it is deleted there is
    /// nothing to point to. Pending merges are left to the merge service, which cancels one whose account is gone.
    /// <para>
    /// rc.16: the whole chain goes. If A was merged into B and B later into C, deleting C removes B→C and then A→B, so A
    /// no longer answers "merged into" the closed account B.
    /// </para>
    /// </summary>
    /// <param name="db">Database.</param>
    /// <param name="deleted">The accounts being deleted.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>How many records were deleted.</returns>
    public static async Task<int> ForgetMergesAsync(SangamDbContext db, IReadOnlyCollection<Guid> deleted, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(deleted);
        int removed = 0;
        HashSet<Guid> seen = [.. deleted];
        List<Guid> survivors = [.. seen];
        for (int hop = 0; hop < MaxMergeHops && survivors.Count > 0; hop++)
        {
            List<Guid> closed = await db.AccountMergeRequests
                .Where(r => r.AppliedAt != null && survivors.Contains(r.SurvivorUserId))
                .Select(r => r.ClosedUserId)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            removed += await db.AccountMergeRequests
                .Where(r => r.AppliedAt != null && survivors.Contains(r.SurvivorUserId))
                .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            survivors = [.. closed.Where(seen.Add)];
        }

        return removed;
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
        user.LoginId = null;
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

    [LoggerMessage(EventId = 1203, Level = LogLevel.Error, Message = "Client secret reminders failed; they will run again at the next sweep.")]
    private partial void LogRemindersFailed(Exception exception);
}
