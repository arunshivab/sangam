namespace Sangam.Identity.Application.Portal;

/// <summary>Outcome of a change-of-e-mail step.</summary>
public enum EmailChangeStatus
{
    /// <summary>A code was sent to the new address.</summary>
    CodeSent,

    /// <summary>The address was changed.</summary>
    Changed,

    /// <summary>The new address is not a valid e-mail address.</summary>
    InvalidEmail,

    /// <summary>The new address is the current one.</summary>
    SameAsCurrent,

    /// <summary>The address cannot be used (taken by another account; deliberately not said).</summary>
    Unavailable,

    /// <summary>The current password was wrong.</summary>
    WrongPassword,

    /// <summary>The account is locked after too many wrong passwords.</summary>
    LockedOut,

    /// <summary>Too many codes requested; try later.</summary>
    RateLimited,

    /// <summary>The code was wrong or expired.</summary>
    WrongCode,

    /// <summary>There is no pending change.</summary>
    NothingPending,
}

/// <summary>Result of a change-of-e-mail step, with a message to show the person.</summary>
/// <param name="Status">Outcome.</param>
/// <param name="Message">Text for the person.</param>
public sealed record EmailChangeResult(EmailChangeStatus Status, string Message);

/// <summary>A pending change, for display.</summary>
/// <param name="NewEmail">The address awaiting confirmation.</param>
/// <param name="RequestedAt">When it was requested.</param>
public sealed record PendingEmailChange(string NewEmail, DateTimeOffset RequestedAt);

/// <summary>
/// Change of e-mail address (OI-022): the current password is required, a code goes to the new
/// address, and on confirmation the old address is told.
/// </summary>
public interface IEmailChangeService
{
    /// <summary>The pending change, if any.</summary>
    /// <param name="userId">The person.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    Task<PendingEmailChange?> GetPendingAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>Starts a change: checks the password and sends a code to <paramref name="newEmail"/>.</summary>
    /// <param name="userId">The person.</param>
    /// <param name="newEmail">The new address.</param>
    /// <param name="currentPassword">The current password.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    Task<EmailChangeResult> RequestAsync(Guid userId, string newEmail, string currentPassword, CancellationToken cancellationToken = default);

    /// <summary>Completes a change with the code sent to the new address.</summary>
    /// <param name="userId">The person.</param>
    /// <param name="code">The code.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    Task<EmailChangeResult> ConfirmAsync(Guid userId, string code, CancellationToken cancellationToken = default);

    /// <summary>Cancels a pending change.</summary>
    /// <param name="userId">The person.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    Task CancelAsync(Guid userId, CancellationToken cancellationToken = default);
}
