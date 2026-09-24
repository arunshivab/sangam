using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using OpenIddict.Abstractions;
using Sangam.Identity.Application.Accounts;
using Sangam.Identity.Application.Tenancy;
using Sangam.Identity.Domain;
using Sangam.Shared.Constants;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Sangam.Identity.Server.Authorization;

/// <summary>
/// Builds the claims for a token from a user and the granted scopes, and decides which token
/// each claim lands in. ID tokens stay small (sub, name, email, email_verified, sangam_orgs);
/// everything else is served by <c>/connect/userinfo</c> from the access token's scopes.
/// </summary>
public static class SangamClaimsBuilder
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Creates the identity for a user-facing token.</summary>
    /// <param name="user">The user.</param>
    /// <param name="scopes">Granted scopes.</param>
    /// <param name="orgs">The user's memberships in the requesting app (may be empty).</param>
    /// <param name="authenticationScheme">Identity authentication type.</param>
    public static ClaimsIdentity Build(UserSummary user, IReadOnlyCollection<string> scopes, IReadOnlyList<OrgClaim> orgs, string authenticationScheme)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(orgs);

        ClaimsIdentity identity = new(authenticationScheme, Claims.Name, Claims.Role);
        identity.SetClaim(Claims.Subject, user.Id.ToString("D"));

        if (scopes.Contains(SangamScopes.Profile))
        {
            identity.SetClaim(Claims.Name, user.DisplayName);
            identity.SetClaim(Claims.GivenName, user.FirstName);
            identity.SetClaim(Claims.FamilyName, user.LastName);
            identity.SetClaim(Claims.Birthdate, user.DateOfBirth.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            identity.SetClaim(Claims.Gender, Genders.ToCode(user.Gender));
            identity.SetClaim(Claims.Locale, user.Locale);
            identity.SetClaim(Claims.Zoneinfo, "Asia/Kolkata");
        }

        if (scopes.Contains(SangamScopes.Email))
        {
            identity.SetClaim(Claims.Email, user.Email);
            identity.SetClaim(Claims.EmailVerified, user.EmailVerified);
        }

        if (scopes.Contains(SangamScopes.Phone) && !string.IsNullOrEmpty(user.Mobile))
        {
            identity.SetClaim(Claims.PhoneNumber, user.Mobile);
            identity.SetClaim(Claims.PhoneNumberVerified, user.MobileVerified);
        }

        if (scopes.Contains(SangamScopes.OrgsRead))
        {
            identity.AddClaim(new Claim(SangamClaims.Orgs, JsonSerializer.Serialize(orgs.Select(ToJson), JsonOptions), "JSON_ARRAY"));
        }

        identity.SetScopes(scopes);
        identity.SetDestinations(GetDestinations);
        return identity;
    }

    /// <summary>Which tokens a claim goes into.</summary>
    /// <param name="claim">The claim.</param>
    public static IEnumerable<string> GetDestinations(Claim claim)
    {
        ArgumentNullException.ThrowIfNull(claim);
        return claim.Type switch
        {
            Claims.Subject or Claims.Name or Claims.Email or Claims.EmailVerified or SangamClaims.Orgs
                => [Destinations.AccessToken, Destinations.IdentityToken],
            "AspNet.Identity.SecurityStamp" => [],
            _ => [Destinations.AccessToken],
        };
    }

    private static Dictionary<string, object> ToJson(OrgClaim o) => new()
    {
        [SangamOrgClaim.Id] = o.Id.ToString("D"),
        [SangamOrgClaim.Name] = o.Name,
        [SangamOrgClaim.Type] = o.Type,
        [SangamOrgClaim.Path] = o.Path,
        [SangamOrgClaim.Role] = o.Role,
        [SangamOrgClaim.Permissions] = o.Permissions,
        [SangamOrgClaim.Inherits] = o.Inherits,
    };
}
