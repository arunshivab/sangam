namespace Sangam.Identity.Domain.Entities;

/// <summary>
/// rc.12 (founder decisions, 14 Oct 2026): a hospital admin adding a person (push). It holds what the admin entered — the
/// hospital's staff record, kept for the audit period — and one activation code the admin hands over in person. The person
/// opens the e-mailed link and must first enter that code; then they create their account, or join with their existing one.
/// One invite covers every organisation added for that person (<see cref="PendingMembership"/>), in every application.
/// </summary>
public sealed class StaffInvite
{
    /// <summary>How long the link and code stay usable.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(14);

    /// <summary>Primary key.</summary>
    public Guid Id { get; set; }

    /// <summary>The application the admin was working in when they added the person (the e-mail's wording follows it).</summary>
    public Guid AppId { get; set; }

    /// <summary>First name as the admin entered it.</summary>
    public string FirstName { get; set; } = string.Empty;

    /// <summary>Last name as the admin entered it.</summary>
    public string LastName { get; set; } = string.Empty;

    /// <summary>Email as the admin entered it.</summary>
    public string Email { get; set; } = string.Empty;

    /// <summary>Upper-case email, for matching.</summary>
    public string NormalizedEmail { get; set; } = string.Empty;

    /// <summary>Mobile as the admin entered it (E.164), if given.</summary>
    public string? Mobile { get; set; }

    /// <summary>Date of birth as the admin entered it.</summary>
    public DateOnly DateOfBirth { get; set; }

    /// <summary>The existing account this invite is for, when the check found one (the person still accepts with the code).</summary>
    public Guid? ExistingUserId { get; set; }

    /// <summary>The fields the admin knowingly accepted as different from the existing account (JSON array), if any.</summary>
    public string AcceptedDifferences { get; set; } = "[]";

    /// <summary>SHA-256 of the link token (the token itself is only in the e-mail).</summary>
    public string TokenHash { get; set; } = string.Empty;

    /// <summary>The activation code, hashed like a password; the admin sees the code once.</summary>
    public string CodeHash { get; set; } = string.Empty;

    /// <summary>Wrong codes entered so far; at five the invite is cancelled.</summary>
    public int FailedCodes { get; set; }

    /// <summary>Who added the person.</summary>
    public Guid CreatedByUserId { get; set; }

    /// <summary>Whether the admin was not DigiLocker-verified, under the application's temporary exception.</summary>
    public bool AddedBeforeDigiLocker { get; set; }

    /// <summary>When it was made.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>When the link and code stop working.</summary>
    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>When the person accepted.</summary>
    public DateTimeOffset? AcceptedAt { get; set; }

    /// <summary>The account that accepted.</summary>
    public Guid? AcceptedByUserId { get; set; }

    /// <summary>When it was cancelled (by an admin, or after five wrong codes).</summary>
    public DateTimeOffset? CancelledAt { get; set; }
}

/// <summary>
/// rc.12: one organisation a person is being added to, with its roles, applied when the person has joined (accepted the
/// invite, or gave a join code) and the start date has come. Until then it is waiting; a background sweep applies it.
/// </summary>
public sealed class PendingMembership
{
    /// <summary>Primary key.</summary>
    public Guid Id { get; set; }

    /// <summary>The invite it belongs to, when it came from one.</summary>
    public Guid? InviteId { get; set; }

    /// <summary>The person, once known.</summary>
    public Guid? UserId { get; set; }

    /// <summary>The application.</summary>
    public Guid AppId { get; set; }

    /// <summary>The organisation.</summary>
    public Guid OrgId { get; set; }

    /// <summary>Role codes (JSON array); empty for staff whose roles the application keeps.</summary>
    public string Roles { get; set; } = "[]";

    /// <summary>The first day of access (India time), if later than the day it is applied.</summary>
    public DateOnly? StartsOn { get; set; }

    /// <summary>When the roles end, if they do.</summary>
    public DateTimeOffset? ExpiresAt { get; set; }

    /// <summary>Who added it.</summary>
    public Guid CreatedByUserId { get; set; }

    /// <summary>When it was added.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>When it became memberships.</summary>
    public DateTimeOffset? AppliedAt { get; set; }

    /// <summary>When it was cancelled instead.</summary>
    public DateTimeOffset? CancelledAt { get; set; }
}

/// <summary>
/// rc.12 (pull): a short one-time code a person makes in the portal ("Join an organisation") and gives to a hospital admin,
/// who enters it to add exactly that account. Only the person can make one, so entering it is their consent.
/// </summary>
public sealed class JoinCode
{
    /// <summary>How long a join code lasts.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(4);

    /// <summary>Primary key.</summary>
    public Guid Id { get; set; }

    /// <summary>The person who made it.</summary>
    public Guid UserId { get; set; }

    /// <summary>SHA-256 of the normalised code.</summary>
    public string CodeHash { get; set; } = string.Empty;

    /// <summary>When it was made.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>When it stops working.</summary>
    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>When an admin used it.</summary>
    public DateTimeOffset? UsedAt { get; set; }

    /// <summary>The admin who used it.</summary>
    public Guid? UsedByUserId { get; set; }
}

/// <summary>
/// rc.12: one check an admin ran before adding a person (which fields matched an existing account, never the values). The
/// rows enforce at most five checks per person per admin per day and stay as evidence.
/// </summary>
public sealed class StaffCheck
{
    /// <summary>Primary key.</summary>
    public Guid Id { get; set; }

    /// <summary>The admin.</summary>
    public Guid AdminUserId { get; set; }

    /// <summary>SHA-256 of the normalised email the check was about.</summary>
    public string SubjectHash { get; set; } = string.Empty;

    /// <summary>What the check found: <c>none</c>, <c>match</c>, <c>differs</c>, <c>similar</c>.</summary>
    public string Outcome { get; set; } = string.Empty;

    /// <summary>The fields that differed (JSON array of field names).</summary>
    public string Differences { get; set; } = "[]";

    /// <summary>When.</summary>
    public DateTimeOffset CheckedAt { get; set; }
}
