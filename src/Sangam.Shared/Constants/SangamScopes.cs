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

    /// <summary>The user's display name and locale.</summary>
    public const string Profile = "profile";

    /// <summary>The user's verified email address.</summary>
    public const string Email = "email";

    /// <summary>Read access to the organisations the user belongs to and their roles there.</summary>
    public const string OrgsRead = "orgs.read";

    /// <summary>Gets every scope Sangam knows how to issue, in canonical order.</summary>
    public static IReadOnlyList<string> All { get; } = [OpenId, Profile, Email, OrgsRead];
}
