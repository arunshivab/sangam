using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Sangam.Identity.Application.Admin;
using Sangam.Identity.Application.Verification;
using Sangam.Identity.Domain.Entities;
using Sangam.Identity.Domain.Enums;
using Sangam.Identity.Infrastructure.Persistence;
using Sangam.Identity.Infrastructure.Verification;

namespace Sangam.Identity.Infrastructure.Admin;

/// <summary>
/// <see cref="IAccountRecoveryService"/> (rc.6, SGM-914): self-service recovery with DigiLocker over the two-step reset
/// requests of D-K. The record is compared with the account — the keyed hash of the DigiLocker id for a person already
/// verified, otherwise <see cref="RecoveryMatch"/> — and a match starts the cooling-off period at once; anything else
/// waits for an operator, who sees only the name, date of birth and gender on both sides. Nobody at Sangam talks to the
/// person or sees a document.
/// </summary>
public sealed partial class EfAccountRecoveryService : IAccountRecoveryService
{
    private readonly SangamDbContext _db;
    private readonly UserManager<SangamUser> _users;
    private readonly EfMfaResetService _resets;
    private readonly EfIdentityVerificationService _verification;

    /// <summary>Initialises the service.</summary>
    /// <param name="db">Database.</param>
    /// <param name="users">Identity user manager.</param>
    /// <param name="resets">The two-step reset requests.</param>
    /// <param name="verification">Identity verification (for the keyed hash).</param>
    public EfAccountRecoveryService(SangamDbContext db, UserManager<SangamUser> users, EfMfaResetService resets, EfIdentityVerificationService verification)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _users = users ?? throw new ArgumentNullException(nameof(users));
        _resets = resets ?? throw new ArgumentNullException(nameof(resets));
        _verification = verification ?? throw new ArgumentNullException(nameof(verification));
    }

    /// <inheritdoc />
    public async Task<RecoveryOutcome> StartAsync(Guid userId, VerifiedIdentity identity, string? ipAddress, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        SangamUser? user = await _users.FindByIdAsync(userId.ToString("D")).ConfigureAwait(false);
        if (user is null || user.Status != UserStatus.Active || !await _users.GetTwoFactorEnabledAsync(user).ConfigureAwait(false))
        {
            return new RecoveryOutcome("none");
        }

        if (await _resets.PendingRowAsync(userId, cancellationToken).ConfigureAwait(false) is not null)
        {
            return new RecoveryOutcome("pending");
        }

        string hash = _verification.SubjectHash(identity.Method, identity.Subject);
        if (await _verification.BelongsToAnotherAsync(userId, identity.Method, hash, cancellationToken).ConfigureAwait(false))
        {
            return new RecoveryOutcome("taken");
        }

        IdentityVerification? verified = await _db.IdentityVerifications.AsNoTracking().FirstOrDefaultAsync(v => v.UserId == userId, cancellationToken).ConfigureAwait(false);
        string match;
        IReadOnlyList<string> mismatches;
        if (verified is not null)
        {
            // Strong match: the same DigiLocker identity that verified this account. Nothing else is compared.
            bool same = verified.Method == identity.Method && string.Equals(verified.SubjectHash, hash, StringComparison.Ordinal);
            match = same ? "strong" : "review";
            mismatches = same ? [] : ["identity"];
        }
        else
        {
            RecoveryMatchResult result = RecoveryMatch.Compare(user.FirstName, user.LastName, user.DateOfBirth, user.Gender, identity);
            match = result.Matches ? "rule" : "review";
            mismatches = result.Mismatches;
        }

        bool privileged = await _resets.IsPrivilegedAsync(userId, cancellationToken).ConfigureAwait(false);
        string record = new RecoveryRecord(identity.Method, hash, identity.Name.Trim(), identity.DateOfBirth, identity.Gender, mismatches).ToJson();
        MfaResetRequest request = await _resets.CreateRecoveryAsync(user, privileged, match, record, ipAddress, cancellationToken).ConfigureAwait(false);
        return match == "review" ? new RecoveryOutcome("review") : new RecoveryOutcome("started", request.EffectiveAt);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RecoveryReview>> WaitingAsync(Guid operatorUserId, CancellationToken cancellationToken = default)
    {
        if (await RoleAsync(operatorUserId, cancellationToken).ConfigureAwait(false) is not (PlatformRole.Support or PlatformRole.Owner))
        {
            return [];
        }

        var rows = await _db.MfaResetRequests.AsNoTracking()
            .Where(r => r.ReviewStatus == MfaResetReview.Waiting && r.CancelledAt == null && r.AppliedAt == null)
            .OrderBy(r => r.RequestedAt)
            .Join(_db.Users, r => r.UserId, u => u.Id, (r, u) => new { r.Id, r.UserId, r.RequestedAt, r.Privileged, r.Record, u.FirstName, u.LastName, u.DateOfBirth, u.Gender })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        List<RecoveryReview> reviews = [];
        foreach (var row in rows)
        {
            if (RecoveryRecord.Parse(row.Record) is not RecoveryRecord record)
            {
                continue;
            }

            reviews.Add(new RecoveryReview(row.Id, row.UserId, row.RequestedAt, row.Privileged, $"{row.FirstName} {row.LastName}".Trim(), row.DateOfBirth, row.Gender, record.Name, record.DateOfBirth, record.Gender, record.Mismatches));
        }

        return reviews;
    }

    /// <inheritdoc />
    public Task<AdminResult> ApproveAsync(Guid operatorUserId, Guid requestId, string reason, string? ipAddress, CancellationToken cancellationToken = default)
        => DecideAsync(operatorUserId, requestId, reason, ipAddress, approve: true, cancellationToken);

    /// <inheritdoc />
    public Task<AdminResult> RefuseAsync(Guid operatorUserId, Guid requestId, string reason, string? ipAddress, CancellationToken cancellationToken = default)
        => DecideAsync(operatorUserId, requestId, reason, ipAddress, approve: false, cancellationToken);

    private async Task<AdminResult> DecideAsync(Guid operatorUserId, Guid requestId, string reason, string? ipAddress, bool approve, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reason);
        PlatformRole? role = await RoleAsync(operatorUserId, cancellationToken).ConfigureAwait(false);
        if (role is not (PlatformRole.Support or PlatformRole.Owner))
        {
            return AdminResult.Refused("Reviewing a recovery needs Support or Owner access.");
        }

        MfaResetRequest? request = await _db.MfaResetRequests.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == requestId && r.ReviewStatus == MfaResetReview.Waiting && r.CancelledAt == null && r.AppliedAt == null, cancellationToken).ConfigureAwait(false);
        if (request is null)
        {
            return AdminResult.Refused("That recovery is no longer waiting for review.");
        }

        if (request.UserId == operatorUserId)
        {
            return AdminResult.Refused("You cannot review the recovery of your own account. Ask another owner.");
        }

        if (request.Privileged && role != PlatformRole.Owner)
        {
            return AdminResult.Refused("Only an Owner can review the recovery of a privileged account.");
        }

        string text = reason.Trim();
        if (text.Length < 10)
        {
            return AdminResult.Refused("Write the reason for your decision, at least a sentence.");
        }

        if (LongNumberRegex().IsMatch(text))
        {
            return AdminResult.Refused("The reason looks like it contains an identity-document number. Never record Aadhaar, PAN or passport numbers.");
        }

        bool done = approve
            ? await _resets.ApproveAsync(request, operatorUserId, text, ipAddress, cancellationToken).ConfigureAwait(false)
            : await _resets.RefuseAsync(request, operatorUserId, text, ipAddress, cancellationToken).ConfigureAwait(false);
        if (!done)
        {
            return AdminResult.Refused("That recovery is no longer waiting for review.");
        }

        return approve
            ? AdminResult.Ok("Approved. The waiting period has started and the person has been told, with a link to cancel.")
            : AdminResult.Ok("Refused. The person has been told they may create a new account.");
    }

    private async Task<PlatformRole?> RoleAsync(Guid operatorUserId, CancellationToken cancellationToken)
        => await _db.PlatformOperators.AsNoTracking()
            .Where(o => o.UserId == operatorUserId && o.RevokedAt == null)
            .Select(o => (PlatformRole?)o.Role)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

    // Eight or more digits in a row look like an identity-document number, which must never be recorded.
    [GeneratedRegex("[0-9]{8,}")]
    private static partial Regex LongNumberRegex();
}
