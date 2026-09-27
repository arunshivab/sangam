using System.Security.Claims;
using Sangam.Shared.Constants;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Sangam.Admin.Web.Components.Common;

/// <summary>Reads the signed-in operator out of the console's cookie.</summary>
public static class OperatorContext
{
    /// <summary>The Sangam user id, or <see langword="null"/> when not signed in.</summary>
    /// <param name="principal">Current principal.</param>
    public static Guid? Id(ClaimsPrincipal? principal)
        => Guid.TryParse(principal?.FindFirst(Claims.Subject)?.Value, out Guid id) ? id : null;

    /// <summary>Display name, falling back to the email address.</summary>
    /// <param name="principal">Current principal.</param>
    public static string Name(ClaimsPrincipal? principal)
        => principal?.FindFirst(Claims.Name)?.Value ?? principal?.FindFirst(Claims.Email)?.Value ?? "Operator";

    /// <summary>The Sangam session this cookie came from.</summary>
    /// <param name="principal">Current principal.</param>
    public static Guid? SessionId(ClaimsPrincipal? principal)
        => Guid.TryParse(principal?.FindFirst(SangamClaims.SessionId)?.Value, out Guid id) ? id : null;
}
