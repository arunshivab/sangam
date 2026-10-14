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

    /// <summary>
    /// R7 (ASVS V3.3.3): the person's security stamp when the sign-in was made, kept only in server-side tokens (codes,
    /// refresh tokens). A refresh is refused once it has changed — a password reset, signing out everywhere, a new
    /// e-mail or a change to two-step sign-in.
    /// </summary>
    public const string SecurityStamp = "sangam_stamp";

    /// <summary>JSON array of organisation memberships scoped to the requesting app; see <see cref="SangamOrgClaim"/> for members.</summary>
    public const string Orgs = "sangam_orgs";

    /// <summary>
    /// rc.12 (A6): the one organisation the person chose to work at for this sign-in ("Working at which branch?"), as one
    /// JSON object shaped like an element of <see cref="Orgs"/>. Present under <c>orgs.read</c> or <c>org.current</c>.
    /// </summary>
    public const string Org = "sangam_org";

    /// <summary>
    /// rc.12 (M12): present while the person uses emergency access ("break-glass") in this application: one JSON object with
    /// <c>id</c>, <c>case</c> (the application's own opaque case reference), <c>org</c> (the organisation id) and
    /// <c>until</c> (Unix seconds; at most 60 minutes after the start). Show a banner and apply your own limits; stop at
    /// <c>until</c> even if the token lives longer. The same name is the authorization request parameter that asks for it,
    /// carrying the case reference.
    /// </summary>
    public const string Emergency = "sangam_emergency";

    /// <summary>The consent-terms version the user last accepted for this app.</summary>
    public const string ConsentVersion = "sangam_consent_version";

    /// <summary>
    /// PR-26: <c>true</c> under the <c>profile</c> scope when the name, date of birth and gender were verified through
    /// DigiLocker; absent otherwise.
    /// </summary>
    public const string IdentityVerified = "sangam_identity_verified";
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

    /// <summary>The first role code held there (kept for clients written before rc.12; absent when there is none).</summary>
    public const string Role = "role";

    /// <summary>rc.12: every role code held there (several allowed; empty for staff whose roles the application keeps).</summary>
    public const string Roles = "roles";

    /// <summary>Permission strings verbatim from the role definition.</summary>
    public const string Permissions = "permissions";

    /// <summary>Always false since rc.12 (a membership is in its own organisation only); kept for clients written earlier.</summary>
    public const string Inherits = "inherits";
}
