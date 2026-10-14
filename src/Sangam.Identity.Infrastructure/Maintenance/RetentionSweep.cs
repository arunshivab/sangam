using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Sangam.Identity.Infrastructure.Persistence;

namespace Sangam.Identity.Infrastructure.Maintenance;

/// <summary>
/// rc.6 (SGM-910 section 6): deletes short-lived records once their retention has passed. Part of the hourly sweep.
/// </summary>
public static class RetentionSweep
{
    /// <summary>One-time codes and passkey challenges are kept this long after they expire.</summary>
    public static readonly TimeSpan CodeRetention = TimeSpan.FromDays(1);

    /// <summary>Invitations, e-mail change requests and two-step reset requests are kept this long once finished.</summary>
    public static readonly TimeSpan RequestRetention = TimeSpan.FromDays(30);

    /// <summary>The SMS log is kept this long.</summary>
    public static readonly TimeSpan SmsRetention = TimeSpan.FromDays(180);

    /// <summary>A closed grievance is kept this many years.</summary>
    public const int GrievanceYears = 3;

    /// <summary>rc.12 (decision 1 of 14 Oct 2026): the hospitals' staff records and the checks admins ran are kept this many
    /// years, with the audit archive.</summary>
    public const int StaffRecordYears = 7;

    /// <summary>Runs the deletions.</summary>
    /// <param name="db">Database.</param>
    /// <param name="now">Current time.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>How many rows were deleted in all.</returns>
    public static async Task<int> RunAsync(SangamDbContext db, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        int removed = 0;

        DateTimeOffset codes = now - CodeRetention;
        removed += await db.OneTimeCodes.Where(c => c.ExpiresAt < codes).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        removed += await db.PasskeyChallenges.Where(c => c.ExpiresAt < codes).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        removed += await db.JoinCodes.Where(c => c.ExpiresAt < codes).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);

        // rc.12: what an admin entered about a person (their staff record), with its waiting additions, and the checks.
        DateTimeOffset staff = now.AddYears(-StaffRecordYears);
        removed += await db.StaffInvites.Where(i => i.ExpiresAt < staff && (i.AcceptedAt == null || i.AcceptedAt < staff)).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        removed += await db.PendingMemberships.Where(p => p.InviteId == null && p.CreatedAt < staff && (p.AppliedAt != null || p.CancelledAt != null)).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        removed += await db.StaffChecks.Where(c => c.CheckedAt < staff).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);

        // rc.12 (M12, T1): emergency access uses are kept as long as staff records; removed shared devices for a year.
        removed += await db.EmergencyAccesses.Where(e => e.StartedAt < staff).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        DateTimeOffset devices = now.AddYears(-1);
        removed += await db.SharedDevices.Where(d => (d.RevokedAt != null && d.RevokedAt < devices) || (d.EnrolledAt == null && d.EnrolExpiresAt < devices)).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);

        DateTimeOffset requests = now - RequestRetention;
        removed += await db.Invitations
            .Where(i => (i.AcceptedAt != null && i.AcceptedAt < requests) || (i.RevokedAt != null && i.RevokedAt < requests) || (i.AcceptedAt == null && i.RevokedAt == null && i.ExpiresAt < requests))
            .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);

        // A pending e-mail change lives only as long as its code (minutes), so one older than the retention is finished too.
        removed += await db.EmailChangeRequests
            .Where(r => (r.CompletedAt != null && r.CompletedAt < requests) || (r.CancelledAt != null && r.CancelledAt < requests) || (r.CompletedAt == null && r.CancelledAt == null && r.CreatedAt < requests))
            .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        removed += await db.MfaResetRequests
            .Where(r => (r.AppliedAt != null && r.AppliedAt < requests) || (r.CancelledAt != null && r.CancelledAt < requests))
            .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);

        // rc.7: a cancelled merge goes after 30 days; an applied one is kept (two ids only) so the closed id can still
        // answer "merged into" to an application that knew it.
        removed += await db.AccountMergeRequests
            .Where(r => r.CancelledAt != null && r.CancelledAt < requests)
            .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);

        DateTimeOffset sms = now - SmsRetention;
        removed += await db.SmsMessages.Where(m => m.CreatedAt < sms).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);

        DateTimeOffset grievances = now.AddYears(-GrievanceYears);
        List<Guid> closed = await db.Grievances
            .Where(g => g.ClosedAt != null && g.ClosedAt < grievances)
            .Select(g => g.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        if (closed.Count > 0)
        {
            // A grievance's history is protected against change by a trigger; only this retention sweep may delete it, in a
            // transaction that sets the maintenance switch (migration Rc6Retention).
            IDbContextTransaction tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using (tx.ConfigureAwait(false))
            {
                await db.Database.ExecuteSqlRawAsync("SELECT set_config('sangam.retention_maintenance', 'on', true);", cancellationToken).ConfigureAwait(false);
                removed += await db.GrievanceEntries.Where(e => closed.Contains(e.GrievanceId)).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
                removed += await db.Grievances.Where(g => closed.Contains(g.Id)).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
                await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        return removed;
    }
}
