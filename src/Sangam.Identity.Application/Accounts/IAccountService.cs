using Sangam.Identity.Domain.Enums;

namespace Sangam.Identity.Application.Accounts;

/// <summary>
/// The account use cases behind screens 1–6. Hosts call these with DTOs and never see the
/// user entity; the implementation lives in Infrastructure on top of ASP.NET Core Identity.
/// </summary>
public interface IAccountService
{
    /// <summary>Creates an unverified account and emails the verification code.</summary>
    /// <param name="command">The registration data.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Success with the new user id in <see cref="RegistrationOutcome.UserId"/>, or field errors.</returns>
    Task<RegistrationOutcome> RegisterAsync(RegisterUserCommand command, CancellationToken cancellationToken = default);

    /// <summary>Finds a user by email. Returns <see langword="null"/> for unknown or hard-deleted addresses.</summary>
    /// <param name="email">Email address (any casing).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<UserSummary?> FindByEmailAsync(string email, CancellationToken cancellationToken = default);

    /// <summary>Finds a user by id.</summary>
    /// <param name="userId">User id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<UserSummary?> FindByIdAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>Issues (or re-issues) a code for <paramref name="purpose"/> and emails it.</summary>
    /// <param name="userId">The user.</param>
    /// <param name="purpose">What the code is for.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<OtpIssueResult> IssueCodeAsync(Guid userId, OneTimeCodePurpose purpose, CancellationToken cancellationToken = default);

    /// <summary>Checks a code; on success it is consumed and, for <see cref="OneTimeCodePurpose.EmailVerification"/>, the address is marked verified.</summary>
    /// <param name="userId">The user.</param>
    /// <param name="purpose">What the code is for.</param>
    /// <param name="code">The six digits the user typed.</param>
    /// <param name="ipAddress">Client IP for the audit log.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<OtpVerifyStatus> VerifyCodeAsync(Guid userId, OneTimeCodePurpose purpose, string code, string? ipAddress, CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks email + password and resolves the effective sign-in mode. Records lockout failures
    /// and audit events. Never reveals whether the email exists.
    /// </summary>
    /// <param name="email">Email address.</param>
    /// <param name="password">Password.</param>
    /// <param name="appPolicy">Policy of the app in the flow, or <see langword="null"/> for a portal sign-in.</param>
    /// <param name="ipAddress">Client IP for the audit log.</param>
    /// <param name="userAgent">Client user agent for the audit log.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<SignInCheck> CheckPasswordAsync(string email, string password, SignInPolicy? appPolicy, string? ipAddress, string? userAgent, CancellationToken cancellationToken = default);

    /// <summary>
    /// Starts a passwordless (<see cref="SignInMode.OtpOnly"/>) sign-in: resolves the mode for the
    /// address and, when it is passwordless, issues a sign-in code. Never reveals whether the email exists.
    /// </summary>
    /// <param name="email">Email address.</param>
    /// <param name="appPolicy">Policy of the app in the flow, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The user and mode when a code was issued; <see langword="null"/> user otherwise.</returns>
    Task<SignInCheck> BeginOtpSignInAsync(string email, SignInPolicy? appPolicy, CancellationToken cancellationToken = default);

    /// <summary>Records a successful sign-in (audit).</summary>
    /// <param name="userId">The user.</param>
    /// <param name="mode">Mode used.</param>
    /// <param name="ipAddress">Client IP.</param>
    /// <param name="userAgent">Client user agent.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task RecordSignInAsync(Guid userId, SignInMode mode, string? ipAddress, string? userAgent, CancellationToken cancellationToken = default);

    /// <summary>Emails a reset code when the address exists; always returns normally (no enumeration).</summary>
    /// <param name="email">Email address.</param>
    /// <param name="ipAddress">Client IP.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The user id when a code was issued, so the reset screen can be pre-bound; otherwise <see langword="null"/>. The screen must not reveal which.</returns>
    Task<Guid?> RequestPasswordResetAsync(string email, string? ipAddress, CancellationToken cancellationToken = default);

    /// <summary>Sets a new password when the reset code is valid and rotates the security stamp (signs out everywhere).</summary>
    /// <param name="email">Email address.</param>
    /// <param name="code">Reset code.</param>
    /// <param name="newPassword">New password.</param>
    /// <param name="ipAddress">Client IP.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<AccountResult> ResetPasswordAsync(string email, string code, string newPassword, string? ipAddress, CancellationToken cancellationToken = default);

    /// <summary>Changes the user's own sign-in preference.</summary>
    /// <param name="userId">The user.</param>
    /// <param name="preference">New preference.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task SetSignInPreferenceAsync(Guid userId, SignInMode preference, CancellationToken cancellationToken = default);
}

/// <summary>Result of <see cref="IAccountService.RegisterAsync"/>.</summary>
/// <param name="Result">Success or field errors.</param>
/// <param name="UserId">The new user's id on success.</param>
public sealed record RegistrationOutcome(AccountResult Result, Guid? UserId);
