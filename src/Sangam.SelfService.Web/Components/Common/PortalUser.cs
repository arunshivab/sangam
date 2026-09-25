using System.Security.Claims;
using Sangam.Shared.Constants;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Sangam.SelfService.Web.Components.Common;

/// <summary>Reads the signed-in user out of the portal's cookie.</summary>
public static class PortalUser
{
    /// <summary>The Sangam user id, or <see langword="null"/> when not signed in.</summary>
    /// <param name="principal">Current principal.</param>
    public static Guid? Id(ClaimsPrincipal? principal)
        => Guid.TryParse(principal?.FindFirst(Claims.Subject)?.Value, out Guid id) ? id : null;

    /// <summary>Display name, falling back to the email address.</summary>
    /// <param name="principal">Current principal.</param>
    public static string Name(ClaimsPrincipal? principal)
        => principal?.FindFirst(Claims.Name)?.Value
        ?? principal?.FindFirst(Claims.Email)?.Value
        ?? "Your account";

    /// <summary>Email address, when the scope was granted.</summary>
    /// <param name="principal">Current principal.</param>
    public static string? Email(ClaimsPrincipal? principal) => principal?.FindFirst(Claims.Email)?.Value;

    /// <summary>
    /// The Sangam browser session this portal cookie was issued from (the OIDC <c>sid</c> claim),
    /// so the session list can mark "This device".
    /// </summary>
    /// <param name="principal">Current principal.</param>
    public static Guid? SessionId(ClaimsPrincipal? principal)
        => Guid.TryParse(principal?.FindFirst(SangamClaims.SessionId)?.Value, out Guid id) ? id : null;

    /// <summary>Two-letter initials for the avatar.</summary>
    /// <param name="principal">Current principal.</param>
    public static string Initials(ClaimsPrincipal? principal)
    {
        string name = Name(principal);
        string[] parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length switch
        {
            0 => "?",
            1 => parts[0][..1].ToUpperInvariant(),
            _ => (parts[0][..1] + parts[^1][..1]).ToUpperInvariant(),
        };
    }
}
