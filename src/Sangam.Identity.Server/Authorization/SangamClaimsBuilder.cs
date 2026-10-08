using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using OpenIddict.Abstractions;
using Sangam.Identity.Application.Accounts;
using Sangam.Identity.Application.Tenancy;
using Sangam.Identity.Domain;
using Sangam.Identity.Server.Authentication;
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
    /// <param name="sessionId">The Sangam browser session the token is issued from (the OIDC <c>sid</c>), when known.</param>
    /// <param name="proof">How and when the person authenticated (PR-17): <c>acr</c>, <c>amr</c>, <c>auth_time</c>.</param>
    /// <param name="custom">PR-25: the application's custom claims, released under the <c>attributes</c> scope.</param>
    public static ClaimsIdentity Build(UserSummary user, IReadOnlyCollection<string> scopes, IReadOnlyList<OrgClaim> orgs, string authenticationScheme, string? sessionId = null, AuthenticationProof? proof = null, IReadOnlyDictionary<string, object>? custom = null)
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

            // Standard OIDC signal: an app compares this with the copy it cached and re-reads
            // /connect/userinfo when it has moved. Sangam never pushes profile changes.
            identity.SetClaim(Claims.UpdatedAt, user.UpdatedAt.ToUnixTimeSeconds());
            if (user.IdentityVerifiedAt is not null)
            {
                identity.SetClaim(SangamClaims.IdentityVerified, true);
            }
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

        if (custom is not null && scopes.Contains(SangamScopes.Attributes))
        {
            foreach ((string name, object value) in custom)
            {
                switch (value)
                {
                    case string[] list:
                        identity.AddClaim(new Claim(name, JsonSerializer.Serialize(list), "JSON_ARRAY"));
                        break;
                    case bool flag:
                        identity.AddClaim(new Claim(name, flag ? "true" : "false", ClaimValueTypes.Boolean));
                        break;
                    case decimal number:
                        identity.AddClaim(new Claim(name, number.ToString(CultureInfo.InvariantCulture), ClaimValueTypes.Double));
                        break;
                    default:
                        identity.SetClaim(name, Convert.ToString(value, CultureInfo.InvariantCulture));
                        break;
                }
            }
        }

        if (!string.IsNullOrEmpty(sessionId))
        {
            identity.SetClaim(SangamClaims.SessionId, sessionId);
        }

        if (proof is not null)
        {
            identity.SetClaim(Claims.AuthenticationContextReference, proof.Acr);
            identity.SetClaims(Claims.AuthenticationMethodReference, [.. proof.Methods]);
            if (proof.AuthenticatedAt is DateTimeOffset at)
            {
                identity.SetClaim(Claims.AuthenticationTime, at.ToUnixTimeSeconds());
            }
        }

        identity.SetScopes(scopes);

        // Destinations are assigned to the claims present now: every claim must be set above this line.
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
            Claims.Subject or Claims.Name or Claims.Email or Claims.EmailVerified or SangamClaims.SessionId or SangamClaims.Orgs
                or Claims.AuthenticationContextReference or Claims.AuthenticationMethodReference or Claims.AuthenticationTime
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

/// <summary>How and when a person authenticated, as tokens carry it (PR-17, OpenID Connect Core §2).</summary>
/// <param name="Acr">The assurance level reached.</param>
/// <param name="Methods">The RFC 8176 methods.</param>
/// <param name="AuthenticatedAt">When, if known.</param>
public sealed record AuthenticationProof(string Acr, IReadOnlyList<string> Methods, DateTimeOffset? AuthenticatedAt)
{
    /// <summary>The proof carried by a Sangam browser session.</summary>
    /// <param name="session">The session principal.</param>
    public static AuthenticationProof FromSession(ClaimsPrincipal session)
    {
        ArgumentNullException.ThrowIfNull(session);
        IReadOnlyList<string> methods = SangamAuthentication.SessionMethods(session);
        return new AuthenticationProof(AuthenticationAssurance.AcrFor(methods), methods, SangamAuthentication.AuthenticatedAt(session));
    }

    /// <summary>The proof already in a token's principal (refresh: it never changes after the sign-in).</summary>
    /// <param name="principal">The stored principal.</param>
    public static AuthenticationProof? FromToken(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        string? acr = principal.GetClaim(Claims.AuthenticationContextReference);
        if (acr is null)
        {
            return null;
        }

        DateTimeOffset? at = long.TryParse(principal.GetClaim(Claims.AuthenticationTime), NumberStyles.None, CultureInfo.InvariantCulture, out long seconds)
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : null;
        return new AuthenticationProof(acr, [.. principal.GetClaims(Claims.AuthenticationMethodReference)], at);
    }
}
