namespace Sangam.Identity.Application.Passkeys;

/// <summary>Options for a passkey ceremony, to hand to the browser.</summary>
/// <param name="ChallengeId">Identifies the ceremony when the browser answers.</param>
/// <param name="OptionsJson">The WebAuthn options, as JSON.</param>
public sealed record PasskeyCeremony(Guid ChallengeId, string OptionsJson);

/// <summary>A passkey on the account, for display.</summary>
/// <param name="Id">Identifier.</param>
/// <param name="Name">The person's name for it.</param>
/// <param name="Synced">Whether it can be synced between devices.</param>
/// <param name="CreatedAt">When it was added.</param>
/// <param name="LastUsedAt">When it was last used.</param>
public sealed record PasskeyRow(Guid Id, string Name, bool Synced, DateTimeOffset CreatedAt, DateTimeOffset? LastUsedAt);

/// <summary>Outcome of a passkey step.</summary>
/// <param name="Succeeded">Whether it worked.</param>
/// <param name="Message">Text for the person.</param>
/// <param name="UserId">The person signed in, after a successful sign-in.</param>
public sealed record PasskeyResult(bool Succeeded, string Message, Guid? UserId = null);

/// <summary>Passkeys (WebAuthn / FIDO2) for sign-in and as a second factor (PR-14, D-117, SGM-205).</summary>
public interface IPasskeyService
{
    /// <summary>Whether passkeys are switched on (<c>Sangam:Passkeys:Enabled</c>).</summary>
    bool Enabled { get; }

    /// <summary>The person's passkeys.</summary>
    /// <param name="userId">The person.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    Task<IReadOnlyList<PasskeyRow>> ListAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>Starts adding a passkey for a signed-in person.</summary>
    /// <param name="userId">The person.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    Task<PasskeyCeremony> BeginRegistrationAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>Verifies the browser's answer and stores the passkey.</summary>
    /// <param name="userId">The person.</param>
    /// <param name="challengeId">The ceremony.</param>
    /// <param name="attestationJson">The browser's PublicKeyCredential, as JSON.</param>
    /// <param name="name">The person's name for it.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    Task<PasskeyResult> CompleteRegistrationAsync(Guid userId, Guid challengeId, string attestationJson, string name, CancellationToken cancellationToken = default);

    /// <summary>Starts a passkey sign-in (discoverable credentials: no e-mail needed).</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    Task<PasskeyCeremony> BeginSignInAsync(CancellationToken cancellationToken = default);

    /// <summary>Verifies the browser's assertion; on success returns the person to sign in.</summary>
    /// <param name="challengeId">The ceremony.</param>
    /// <param name="assertionJson">The browser's PublicKeyCredential, as JSON.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    Task<PasskeyResult> CompleteSignInAsync(Guid challengeId, string assertionJson, CancellationToken cancellationToken = default);

    /// <summary>Removes a passkey (kept, marked removed).</summary>
    /// <param name="userId">The person.</param>
    /// <param name="passkeyId">The passkey.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    Task<bool> RemoveAsync(Guid userId, Guid passkeyId, CancellationToken cancellationToken = default);
}
