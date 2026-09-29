using System.Security.Claims;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Sangam.Partner.Web.Components.Common;

/// <summary>Reads the signed-in person out of the partner console's cookie.</summary>
public static class SignedInUser
{
    /// <summary>The Sangam user id, or <see langword="null"/> when not signed in.</summary>
    /// <param name="principal">Current principal.</param>
    public static Guid? Id(ClaimsPrincipal? principal)
        => Guid.TryParse(principal?.FindFirst(Claims.Subject)?.Value, out Guid id) ? id : null;

    /// <summary>Display name, falling back to the email address.</summary>
    /// <param name="principal">Current principal.</param>
    public static string Name(ClaimsPrincipal? principal)
        => principal?.FindFirst(Claims.Name)?.Value ?? principal?.FindFirst(Claims.Email)?.Value ?? "Administrator";
}
