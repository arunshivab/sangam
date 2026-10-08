namespace Sangam.Identity.Application.Accounts;

/// <summary>A two-step reset waiting out its cooling-off period (D-K).</summary>
/// <param name="Id">The request.</param>
/// <param name="RequestedAt">When support asked for it.</param>
/// <param name="EffectiveAt">When it takes effect, unless cancelled.</param>
/// <param name="Privileged">Whether the account is privileged (72 hours instead of 24).</param>
/// <param name="VerificationMethod">How support verified the person.</param>
public sealed record PendingTwoStepReset(Guid Id, DateTimeOffset RequestedAt, DateTimeOffset EffectiveAt, bool Privileged, string VerificationMethod);

/// <summary>
/// The owner's side of a support reset of two-step sign-in (D-K): cancel it ("this wasn't me") from the e-mail
/// link or the notice at sign-in, and the background step that applies a request once its period has passed.
/// </summary>
public interface IMfaResetService
{
    /// <summary>The pending reset on an account, if any.</summary>
    /// <param name="userId">The account.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<PendingTwoStepReset?> PendingAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>Whether <paramref name="token"/> is the cancel token of a reset that is still pending.</summary>
    /// <param name="token">The token from the e-mail.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<PendingTwoStepReset?> FindByTokenAsync(string token, CancellationToken cancellationToken = default);

    /// <summary>Cancels the pending reset behind the e-mail's token; false when it is unknown, already applied or already cancelled.</summary>
    /// <param name="token">The token.</param>
    /// <param name="ipAddress">The visitor's IP address.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<bool> CancelByTokenAsync(string token, string? ipAddress, CancellationToken cancellationToken = default);

    /// <summary>Cancels the account's pending reset from the notice at sign-in.</summary>
    /// <param name="userId">The signed-in owner.</param>
    /// <param name="ipAddress">Their IP address.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<bool> CancelByOwnerAsync(Guid userId, string? ipAddress, CancellationToken cancellationToken = default);

    /// <summary>Whether the owner still has to see the sign-in notice for a pending reset.</summary>
    /// <param name="userId">The account.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<bool> NoticeDueAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>Records that the owner saw the sign-in notice and chose to let the reset go ahead.</summary>
    /// <param name="userId">The account.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task AcknowledgeNoticeAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>Applies every reset whose cooling-off period has passed uncancelled; returns how many.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<int> ApplyDueAsync(CancellationToken cancellationToken = default);
}
