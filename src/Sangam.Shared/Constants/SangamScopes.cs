namespace Sangam.Shared.Constants;

/// <summary>
/// OAuth 2.0 / OpenID Connect scope names that Sangam issues.
/// These are wire-level identifiers: partner applications request them,
/// the consent screen displays them, and the token endpoint grants them.
/// </summary>
public static class SangamScopes
{
    /// <summary>Required for every OpenID Connect request; yields the <c>sub</c> claim.</summary>
    public const string OpenId = "openid";

    /// <summary>Name, given/family name, birthdate, gender, locale, zoneinfo, updated_at.</summary>
    public const string Profile = "profile";

    /// <summary>The user's email address and whether it is verified.</summary>
    public const string Email = "email";

    /// <summary>The user's mobile number and whether it is verified.</summary>
    public const string Phone = "phone";

    /// <summary>Read access to the organisations the user belongs to and their roles there (<c>sangam_orgs</c>).</summary>
    public const string OrgsRead = "orgs.read";

    /// <summary>Refresh tokens (standard <c>offline_access</c>).</summary>
    public const string OfflineAccess = "offline_access";

    /// <summary>Machine-to-machine management of the app's own roles, organisations and memberships (client credentials only).</summary>
    public const string Manage = "sangam.manage";

    /// <summary>
    /// PR-25: the application's custom claims — filled from the attributes it keeps about the person, or from their
    /// memberships — in its tokens and userinfo. The consent screen lists them with their values.
    /// </summary>
    public const string Attributes = "attributes";

    /// <summary>Scopes a user can be asked to consent to, in canonical order.</summary>
    public static IReadOnlyList<string> UserScopes { get; } = [OpenId, Profile, Email, Phone, OrgsRead, Attributes, OfflineAccess];

    /// <summary>Every scope Sangam knows how to issue, in canonical order.</summary>
    public static IReadOnlyList<string> All { get; } = [OpenId, Profile, Email, Phone, OrgsRead, Attributes, OfflineAccess, Manage];
}
