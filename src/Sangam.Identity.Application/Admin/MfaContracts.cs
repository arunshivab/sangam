namespace Sangam.Identity.Application.Admin;

/// <summary>What the enrolment screen needs to show an authenticator app to the user.</summary>
/// <param name="SharedKey">The base32 secret, formatted in groups for typing by hand.</param>
/// <param name="AuthenticatorUri">The <c>otpauth://</c> URI the QR code encodes.</param>
/// <param name="QrCodeSvg">The same URI as an inline SVG QR code — nothing is fetched from anywhere.</param>
public sealed record MfaEnrolment(string SharedKey, string AuthenticatorUri, string QrCodeSvg);

/// <summary>Outcome of confirming an authenticator enrolment.</summary>
/// <param name="Succeeded">Whether the code matched and MFA is now on.</param>
/// <param name="RecoveryCodes">One-time recovery codes, shown once and never again.</param>
public sealed record MfaConfirmation(bool Succeeded, IReadOnlyList<string> RecoveryCodes);

/// <summary>Why a second factor was refused.</summary>
public enum MfaResult
{
    /// <summary>Accepted.</summary>
    Valid = 0,

    /// <summary>Wrong or expired code.</summary>
    Invalid = 1,

    /// <summary>Too many wrong codes; the account is locked for a while.</summary>
    LockedOut = 2,
}

/// <summary>Authenticator-app (TOTP) enrolment and verification.</summary>
public interface IMfaService
{
    /// <summary>Whether the user has an authenticator app enrolled.</summary>
    /// <param name="userId">The user.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<bool> IsEnrolledAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>Starts enrolment: returns the shared key and the URI for the QR code.</summary>
    /// <param name="userId">The user.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<MfaEnrolment> BeginEnrolmentAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>Confirms enrolment with a code from the app and issues recovery codes.</summary>
    /// <param name="userId">The user.</param>
    /// <param name="code">Code from the authenticator app.</param>
    /// <param name="ipAddress">Client IP, for the audit entry.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<MfaConfirmation> ConfirmEnrolmentAsync(Guid userId, string code, string? ipAddress, CancellationToken cancellationToken = default);

    /// <summary>Checks a code from the authenticator app, or a recovery code.</summary>
    /// <param name="userId">The user.</param>
    /// <param name="code">Code to check.</param>
    /// <param name="ipAddress">Client IP, for the audit entry.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<MfaResult> VerifyAsync(Guid userId, string code, string? ipAddress, CancellationToken cancellationToken = default);

    /// <summary>Removes the authenticator. Refused for a platform operator, where MFA is mandatory.</summary>
    /// <param name="userId">The user.</param>
    /// <param name="ipAddress">Client IP, for the audit entry.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<bool> DisableAsync(Guid userId, string? ipAddress, CancellationToken cancellationToken = default);
}
