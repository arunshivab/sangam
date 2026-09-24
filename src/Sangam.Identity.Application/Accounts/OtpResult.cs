namespace Sangam.Identity.Application.Accounts;

/// <summary>Outcome of issuing a one-time code.</summary>
public enum OtpIssueStatus
{
    /// <summary>A fresh code was emailed.</summary>
    Sent = 0,

    /// <summary>A code was sent recently; wait before asking for another.</summary>
    TooSoon = 1,

    /// <summary>The hourly limit for this user and purpose is spent.</summary>
    RateLimited = 2,
}

/// <summary>Result of issuing a one-time code.</summary>
/// <param name="Status">Outcome.</param>
/// <param name="RetryAfter">When the next code may be requested (UTC).</param>
public sealed record OtpIssueResult(OtpIssueStatus Status, DateTimeOffset RetryAfter);

/// <summary>Outcome of checking a one-time code.</summary>
public enum OtpVerifyStatus
{
    /// <summary>Code accepted and consumed.</summary>
    Valid = 0,

    /// <summary>Wrong code; attempts remain.</summary>
    Invalid = 1,

    /// <summary>Expired, exhausted or never issued; a new code must be requested.</summary>
    Expired = 2,
}
