namespace Sangam.Shared.Constants;

/// <summary>
/// Sangam-specific claim names carried in ID and access tokens alongside the standard
/// OpenID Connect claims (<c>sub</c>, <c>email</c>, <c>name</c>, …).
/// </summary>
public static class SangamClaims
{
    /// <summary>
    /// Standard OpenID Connect session identifier (<c>sid</c>): the Sangam browser session the
    /// token was issued from. A client uses it to tell which of the user's sessions is its own.
    /// </summary>
    public const string SessionId = "sid";

    /// <summary>JSON array of organisation memberships scoped to the requesting app; see <see cref="SangamOrgClaim"/> for members.</summary>
    public const string Orgs = "sangam_orgs";

    /// <summary>The consent-terms version the user last accepted for this app.</summary>
    public const string ConsentVersion = "sangam_consent_version";
}

/// <summary>Member names inside each element of the <see cref="SangamClaims.Orgs"/> array.</summary>
public static class SangamOrgClaim
{
    /// <summary>Organisation id.</summary>
    public const string Id = "id";

    /// <summary>Organisation display name.</summary>
    public const string Name = "name";

    /// <summary>Organisation type code.</summary>
    public const string Type = "type";

    /// <summary>Materialised ancestor path, root first.</summary>
    public const string Path = "path";

    /// <summary>Role code in the app's vocabulary.</summary>
    public const string Role = "role";

    /// <summary>Permission strings verbatim from the role definition.</summary>
    public const string Permissions = "permissions";

    /// <summary>Whether the role also applies to descendant organisations.</summary>
    public const string Inherits = "inherits";
}
