namespace Sangam.Identity.Domain.Entities;

/// <summary>
/// The latest state one host reports about something only it can see (V-10), for example the identity server's
/// breached-password list: the operator console shows the identity server's own answer, not its own settings.
/// </summary>
public class HostReport
{
    /// <summary>The reporting host (identity, portal, admin, partner).</summary>
    public string Host { get; set; } = string.Empty;

    /// <summary>What is reported, for example <c>breach_list</c>.</summary>
    public string Subject { get; set; } = string.Empty;

    /// <summary>The report, as JSON.</summary>
    public string Payload { get; set; } = string.Empty;

    /// <summary>When the host last reported it.</summary>
    public DateTimeOffset ReportedAt { get; set; }
}

/// <summary>
/// A message one of the hosts would have sent, in Development only (V-11): every host's outbox writes here, so the
/// identity server's <c>/dev/outbox</c> also shows the e-mails and texts the consoles raise.
/// </summary>
public class DevOutboxMessage
{
    /// <summary>Row number (newest is largest).</summary>
    public long Id { get; set; }

    /// <summary>When it was captured.</summary>
    public DateTimeOffset SentAt { get; set; }

    /// <summary>The host that sent it.</summary>
    public string Host { get; set; } = string.Empty;

    /// <summary><c>email</c> or <c>sms</c>.</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>The recipient: an address or an E.164 number.</summary>
    public string Recipient { get; set; } = string.Empty;

    /// <summary>The subject (e-mail), or the template and sender (SMS).</summary>
    public string Subject { get; set; } = string.Empty;

    /// <summary>The text body.</summary>
    public string Body { get; set; } = string.Empty;
}

/// <summary>
/// D-L, R7 (PR-33): wrong passwords for an address that has no account, counted like a real account's so the lockout
/// cannot tell anyone whether an address has an account. Kept in the database, so every identity-server replica counts
/// the same attempts. The address is kept only as a SHA-256 hash.
/// </summary>
public class UnknownAddressAttempt
{
    /// <summary>SHA-256 of the upper-cased address, hex.</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>Wrong passwords since the last lockout.</summary>
    public int Failures { get; set; }

    /// <summary>When the "lockout" ends, if one is in force.</summary>
    public DateTimeOffset? LockedUntil { get; set; }

    /// <summary>The last attempt.</summary>
    public DateTimeOffset LastSeen { get; set; }
}
