using Sangam.Identity.Domain.Enums;

namespace Sangam.Identity.Domain.Entities;

/// <summary>
/// rc.12 (T1; founder decisions, 14 Oct 2026): a tablet or computer at a nursing station, used by many people in turn,
/// enrolled by a customer or branch admin from the device itself. The browser keeps a random device key; Sangam keeps only
/// its hash. On an enrolled device the sign-in page shows "Who's using this tablet?", and a person whose session an
/// application locked may come back within two hours with their PIN.
/// </summary>
public sealed class SharedDevice
{
    /// <summary>Primary key.</summary>
    public Guid Id { get; set; }

    /// <summary>The application whose organisation tree the device belongs to.</summary>
    public Guid AppId { get; set; }

    /// <summary>The organisation (ward, branch) the device is at.</summary>
    public Guid OrgId { get; set; }

    /// <summary>The label the admin gave it ("Ward 4B · Nursing station").</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>SHA-256 of the device key, once enrolled.</summary>
    public string? KeyHash { get; set; }

    /// <summary>SHA-256 of the one-time enrolment token, while enrolment is in progress.</summary>
    public string? EnrolTokenHash { get; set; }

    /// <summary>When the enrolment token stops working.</summary>
    public DateTimeOffset? EnrolExpiresAt { get; set; }

    /// <summary>The admin who enrolled it.</summary>
    public Guid EnrolledByUserId { get; set; }

    /// <summary>When enrolment started.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>When the device took its key.</summary>
    public DateTimeOffset? EnrolledAt { get; set; }

    /// <summary>When someone last signed in or unlocked on it.</summary>
    public DateTimeOffset? LastSeenAt { get; set; }

    /// <summary>When it was removed (lost, stolen, moved).</summary>
    public DateTimeOffset? RevokedAt { get; set; }

    /// <summary>Who removed it.</summary>
    public Guid? RevokedByUserId { get; set; }
}

/// <summary>rc.12 (T1): one person on one shared device: their last full sign-in there, and their lock.</summary>
public sealed class SharedDevicePerson
{
    /// <summary>Primary key.</summary>
    public Guid Id { get; set; }

    /// <summary>The device.</summary>
    public Guid DeviceId { get; set; }

    /// <summary>The person.</summary>
    public Guid UserId { get; set; }

    /// <summary>Their latest Sangam session on the device.</summary>
    public Guid? SessionId { get; set; }

    /// <summary>When they last signed in fully on it (password, passkey or code).</summary>
    public DateTimeOffset LastFullSignInAt { get; set; }

    /// <summary>How they signed in then.</summary>
    public SignInMode SignInMode { get; set; }

    /// <summary>The RFC 8176 methods of that sign-in (space-separated).</summary>
    public string Methods { get; set; } = string.Empty;

    /// <summary>Whether that sign-in included a second factor.</summary>
    public bool SecondFactor { get; set; }

    /// <summary>When they were last seen on it (sign-in or unlock).</summary>
    public DateTimeOffset LastSeenAt { get; set; }

    /// <summary>When an application locked their session on it; cleared on unlock.</summary>
    public DateTimeOffset? LockedAt { get; set; }

    /// <summary>The session in which they were offered to set a PIN (so the offer is made once per sign-in).</summary>
    public Guid? PinOfferedSessionId { get; set; }
}

/// <summary>
/// rc.12 (T1): a person's 6-digit PIN for unlocking a shared ward device, hashed like a password. It works only on the
/// enrolled device where they last signed in fully, within two hours of a lock and twelve hours of that sign-in; five wrong
/// PINs cancel it until their next full sign-in.
/// </summary>
public sealed class UserPin
{
    /// <summary>The person (primary key).</summary>
    public Guid UserId { get; set; }

    /// <summary>The PIN's hash.</summary>
    public string PinHash { get; set; } = string.Empty;

    /// <summary>When it was set.</summary>
    public DateTimeOffset SetAt { get; set; }

    /// <summary>Wrong PINs since the last full sign-in.</summary>
    public int FailedCount { get; set; }

    /// <summary>When five wrong PINs cancelled it; cleared at the next full sign-in.</summary>
    public DateTimeOffset? CancelledAt { get; set; }
}
