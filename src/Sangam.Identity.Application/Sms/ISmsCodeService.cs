using Sangam.Identity.Application.Accounts;
using Sangam.Identity.Domain.Enums;

namespace Sangam.Identity.Application.Sms;

/// <summary>Why an SMS code was or was not sent.</summary>
public enum SmsIssueStatus
{
    /// <summary>Sent to the provider.</summary>
    Sent = 0,

    /// <summary>A code was sent less than a minute ago.</summary>
    TooSoon = 1,

    /// <summary>The hourly limit for this account, this number or this network has been reached.</summary>
    RateLimited = 2,

    /// <summary>The account has no mobile number.</summary>
    NoMobile = 3,

    /// <summary>Sign-in codes go only to a verified mobile.</summary>
    MobileNotVerified = 4,

    /// <summary>SMS is not offered for this country code (SMS-pumping protection; SGM-206 open question 3).</summary>
    CountryNotAllowed = 5,

    /// <summary>SMS is switched off.</summary>
    Disabled = 6,

    /// <summary>Every provider refused the message.</summary>
    ProviderFailed = 7,

    /// <summary>The account does not exist or cannot sign in.</summary>
    NoAccount = 8,
}

/// <summary>The outcome of asking for an SMS code.</summary>
/// <param name="Status">What happened.</param>
/// <param name="RetryAt">When another code may be asked for, if known.</param>
public sealed record SmsIssueResult(SmsIssueStatus Status, DateTimeOffset? RetryAt = null);

/// <summary>A delivery report, in Sangam's provider-neutral form.</summary>
/// <param name="Provider">The provider's name in settings.</param>
/// <param name="ProviderMessageId">The provider's id for the message.</param>
/// <param name="Delivered">Whether the handset received it.</param>
/// <param name="At">When the provider says it happened.</param>
public sealed record SmsDeliveryReport(string Provider, string ProviderMessageId, bool Delivered, DateTimeOffset At);

/// <summary>
/// SMS one-time codes (PR-15, SGM-206): mobile verification and sign-in. Codes follow the same rules as
/// e-mailed ones (6 digits, 10 minutes, 5 tries, 60-second resend, 5 an hour) and are stored only as hashes.
/// </summary>
public interface ISmsCodeService
{
    /// <summary>Whether SMS is switched on.</summary>
    bool Enabled { get; }

    /// <summary>Whether <paramref name="e164"/> is in a country SMS is offered for.</summary>
    /// <param name="e164">The number in E.164 form.</param>
    bool IsCountryAllowed(string? e164);

    /// <summary>Texts a code that proves control of the account's mobile number.</summary>
    /// <param name="userId">The account.</param>
    /// <param name="ipAddress">The requester's IP, for per-network limits.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<SmsIssueResult> SendMobileVerificationAsync(Guid userId, string? ipAddress, CancellationToken cancellationToken = default);

    /// <summary>Checks a mobile-verification code; on success the mobile becomes verified.</summary>
    /// <param name="userId">The account.</param>
    /// <param name="code">The typed code.</param>
    /// <param name="ipAddress">The requester's IP.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<OtpVerifyStatus> VerifyMobileAsync(Guid userId, string code, string? ipAddress, CancellationToken cancellationToken = default);

    /// <summary>Texts a sign-in code to the account's verified mobile.</summary>
    /// <param name="userId">The account.</param>
    /// <param name="ipAddress">The requester's IP.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<SmsIssueResult> SendSignInCodeAsync(Guid userId, string? ipAddress, CancellationToken cancellationToken = default);

    /// <summary>When another code for <paramref name="purpose"/> may be sent, or <see langword="null"/> when now.</summary>
    /// <param name="userId">The account.</param>
    /// <param name="purpose"><see cref="OneTimeCodePurpose.MobileVerification"/> or <see cref="OneTimeCodePurpose.SmsSignIn"/>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<DateTimeOffset?> NextSendAllowedAtAsync(Guid userId, OneTimeCodePurpose purpose, CancellationToken cancellationToken = default);

    /// <summary>Applies a provider's delivery report. Returns <see langword="false"/> when no message matches.</summary>
    /// <param name="report">The report.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<bool> RecordDeliveryAsync(SmsDeliveryReport report, CancellationToken cancellationToken = default);
}
