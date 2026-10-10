using Sangam.Identity.Application.Admin;
using Sangam.Identity.Domain.Enums;

namespace Sangam.Identity.Application.Verification;

/// <summary>The outcome of asking to recover an account with DigiLocker (rc.6, SGM-914 section 5).</summary>
/// <param name="Code">
/// <c>started</c> (the record matched; the waiting period runs), <c>review</c> (it did not; an operator reviews it),
/// <c>pending</c> (a recovery or reset is already waiting), <c>taken</c> (this DigiLocker identity verifies another
/// account), <c>none</c> (the account has no second step to recover).
/// </param>
/// <param name="EffectiveAt">When it takes effect, for <c>started</c>.</param>
public sealed record RecoveryOutcome(string Code, DateTimeOffset? EffectiveAt = null);

/// <summary>A recovery waiting for an operator: the profile beside the DigiLocker record, and what did not match (rc.6).</summary>
/// <param name="RequestId">The request.</param>
/// <param name="UserId">The account.</param>
/// <param name="RequestedAt">When it was asked for.</param>
/// <param name="Privileged">Whether the account is privileged.</param>
/// <param name="ProfileName">The profile's name.</param>
/// <param name="ProfileDateOfBirth">The profile's date of birth.</param>
/// <param name="ProfileGender">The profile's gender.</param>
/// <param name="RecordName">The record's name.</param>
/// <param name="RecordDateOfBirth">The record's date of birth.</param>
/// <param name="RecordGender">The record's gender.</param>
/// <param name="Mismatches">What did not match (<see cref="RecoveryMatch"/>).</param>
public sealed record RecoveryReview(
    Guid RequestId,
    Guid UserId,
    DateTimeOffset RequestedAt,
    bool Privileged,
    string ProfileName,
    DateOnly ProfileDateOfBirth,
    Gender ProfileGender,
    string RecordName,
    DateOnly RecordDateOfBirth,
    Gender RecordGender,
    IReadOnlyList<string> Mismatches);

/// <summary>
/// rc.6 (SGM-914): a person who has lost their second step and their recovery codes recovers their account with
/// DigiLocker, without support staff. A record that matches the account starts the usual waiting period (24 hours, 72
/// for a privileged account); one that does not waits for an operator, who sees only the name, date of birth and
/// gender on both sides.
/// </summary>
public interface IAccountRecoveryService
{
    /// <summary>Starts a recovery for a person who has passed their first step, with what DigiLocker said.</summary>
    /// <param name="userId">The account.</param>
    /// <param name="identity">The DigiLocker record (its id is hashed at once and never kept).</param>
    /// <param name="ipAddress">The caller's address.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<RecoveryOutcome> StartAsync(Guid userId, VerifiedIdentity identity, string? ipAddress, CancellationToken cancellationToken = default);

    /// <summary>The recoveries waiting for review, oldest first; empty for someone without console access.</summary>
    /// <param name="operatorUserId">The operator.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IReadOnlyList<RecoveryReview>> WaitingAsync(Guid operatorUserId, CancellationToken cancellationToken = default);

    /// <summary>Approves a recovery after review: its waiting period starts and the person is told again.</summary>
    /// <param name="operatorUserId">The operator (an Owner for a privileged account).</param>
    /// <param name="requestId">The request.</param>
    /// <param name="reason">Why the record is the same person.</param>
    /// <param name="ipAddress">The operator's address.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<AdminResult> ApproveAsync(Guid operatorUserId, Guid requestId, string reason, string? ipAddress, CancellationToken cancellationToken = default);

    /// <summary>Refuses a recovery after review; the person is told they may create a new account.</summary>
    /// <param name="operatorUserId">The operator (an Owner for a privileged account).</param>
    /// <param name="requestId">The request.</param>
    /// <param name="reason">Why it is refused.</param>
    /// <param name="ipAddress">The operator's address.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<AdminResult> RefuseAsync(Guid operatorUserId, Guid requestId, string reason, string? ipAddress, CancellationToken cancellationToken = default);
}
