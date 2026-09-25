using Sangam.Identity.Domain.Enums;

namespace Sangam.Identity.Application.Portal;

/// <summary>An application the user has granted access to, as the portal shows it.</summary>
/// <param name="AppId">App id.</param>
/// <param name="ClientId">OAuth client id.</param>
/// <param name="DisplayName">App name.</param>
/// <param name="Description">One-line description.</param>
/// <param name="OwnerCompanyName">Who operates it.</param>
/// <param name="BrandColour">Hex brand colour for the tile.</param>
/// <param name="Glyph">One or two characters for the tile.</param>
/// <param name="PrivacyUrl">The app's privacy policy.</param>
/// <param name="LinkedAt">When access was first granted.</param>
/// <param name="LastUsedAt">When a token was last issued to this app for this user.</param>
/// <param name="Scopes">Scopes the user consented to.</param>
/// <param name="Organisations">Organisations the user holds a role in for this app.</param>
/// <param name="LiveTokens">Live tokens the app currently holds.</param>
public sealed record LinkedApp(
    Guid AppId,
    string ClientId,
    string DisplayName,
    string? Description,
    string OwnerCompanyName,
    string BrandColour,
    string Glyph,
    string? PrivacyUrl,
    DateTimeOffset LinkedAt,
    DateTimeOffset? LastUsedAt,
    IReadOnlyList<string> Scopes,
    IReadOnlyList<string> Organisations,
    int LiveTokens)
{
    /// <summary>Status used for the cord-dot: active, expiring or dormant.</summary>
    /// <param name="now">Current time.</param>
    public LinkedAppStatus StatusAt(DateTimeOffset now)
    {
        DateTimeOffset reference = LastUsedAt ?? LinkedAt;
        TimeSpan idle = now - reference;
        return idle > TimeSpan.FromDays(90) ? LinkedAppStatus.Dormant
            : idle > TimeSpan.FromDays(30) ? LinkedAppStatus.Expiring
            : LinkedAppStatus.Active;
    }
}

/// <summary>How recently an app was used.</summary>
public enum LinkedAppStatus
{
    /// <summary>Used within the last 30 days.</summary>
    Active = 0,

    /// <summary>Not used for 30 to 90 days.</summary>
    Expiring = 1,

    /// <summary>Not used for over 90 days; the portal leads with Revoke.</summary>
    Dormant = 2,
}

/// <summary>A browser session, as the portal shows it.</summary>
/// <param name="Id">Session id.</param>
/// <param name="DeviceLabel">Label the partner app supplied, if any.</param>
/// <param name="Browser">Browser and platform derived from the user agent.</param>
/// <param name="IpAddress">Client IP.</param>
/// <param name="AppName">App whose sign-in started the session, if any.</param>
/// <param name="SignInMode">How the user signed in.</param>
/// <param name="CreatedAt">When the session started.</param>
/// <param name="LastSeenAt">When it was last seen.</param>
/// <param name="IsCurrent">Whether this is the session viewing the portal.</param>
public sealed record PortalSession(
    Guid Id,
    string? DeviceLabel,
    string Browser,
    string? IpAddress,
    string? AppName,
    SignInMode SignInMode,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastSeenAt,
    bool IsCurrent);

/// <summary>One line of the user's audit trail, already phrased for a person.</summary>
/// <param name="Id">Event id.</param>
/// <param name="OccurredAt">When it happened.</param>
/// <param name="Action">The raw action name.</param>
/// <param name="Summary">A sentence a non-technical user can read.</param>
/// <param name="Actor">Who did it ("You", an app name, "A Sangam operator", "Sangam").</param>
/// <param name="IpAddress">Client IP, when the event carried one.</param>
/// <param name="IsSecuritySensitive">Whether to mark the row (failed sign-ins, password changes, revocations).</param>
public sealed record AuditLine(
    long Id,
    DateTimeOffset OccurredAt,
    string Action,
    string Summary,
    string Actor,
    string? IpAddress,
    bool IsSecuritySensitive);

/// <summary>A page of audit lines.</summary>
/// <param name="Lines">The lines, newest first.</param>
/// <param name="Total">Total matching events in the range.</param>
/// <param name="Page">Zero-based page index.</param>
/// <param name="PageSize">Lines per page.</param>
public sealed record AuditPage(IReadOnlyList<AuditLine> Lines, int Total, int Page, int PageSize)
{
    /// <summary>Number of pages.</summary>
    public int PageCount => PageSize <= 0 ? 0 : (int)Math.Ceiling(Total / (double)PageSize);
}

/// <summary>Counters for the dashboard's three stat cards.</summary>
/// <param name="LinkedApps">Apps with live access.</param>
/// <param name="Organisations">Distinct organisations the user belongs to.</param>
/// <param name="ActiveSessions">Live browser sessions.</param>
/// <param name="LastSignInAt">When the user last signed in successfully.</param>
/// <param name="TwoFactorSuggested">Whether to show the 2FA nudge (the user signs in with a password only).</param>
public sealed record PortalOverview(int LinkedApps, int Organisations, int ActiveSessions, DateTimeOffset? LastSignInAt, bool TwoFactorSuggested);

/// <summary>State of a pending account deletion.</summary>
/// <param name="Requested">Whether deletion has been requested.</param>
/// <param name="PurgeAfter">When the account will be purged.</param>
/// <param name="OnHold">Whether a platform operator has placed a hold that blocks the purge.</param>
public sealed record DeletionState(bool Requested, DateTimeOffset? PurgeAfter, bool OnHold);

/// <summary>The profile fields a user may change themselves.</summary>
/// <param name="FirstName">Given name.</param>
/// <param name="LastName">Family name.</param>
/// <param name="Locale">BCP-47 language tag.</param>
/// <param name="Gender">Stated gender.</param>
/// <param name="CountryIso">ISO country of the mobile number.</param>
/// <param name="MobileNumber">National mobile number.</param>
public sealed record ProfileUpdate(string FirstName, string LastName, string Locale, Gender Gender, string CountryIso, string MobileNumber);

/// <summary>Outcome of a profile update.</summary>
/// <param name="Succeeded">Whether it was saved.</param>
/// <param name="Field">The field at fault, when it was not.</param>
/// <param name="Message">What to tell the user.</param>
/// <param name="MobileChanged">Whether the mobile number changed, and so is unverified again.</param>
public sealed record ProfileUpdateResult(bool Succeeded, string? Field, string? Message, bool MobileChanged)
{
    /// <summary>A successful result.</summary>
    /// <param name="mobileChanged">Whether the mobile number changed.</param>
    public static ProfileUpdateResult Ok(bool mobileChanged) => new(true, null, null, mobileChanged);

    /// <summary>A rejected result.</summary>
    /// <param name="field">The field at fault.</param>
    /// <param name="message">What to tell the user.</param>
    public static ProfileUpdateResult Failed(string field, string message) => new(false, field, message, false);
}
