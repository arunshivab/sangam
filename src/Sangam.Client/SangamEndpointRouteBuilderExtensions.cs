using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Sangam.Client;

/// <summary>Maps the sign-out endpoint.</summary>
public static class SangamEndpointRouteBuilderExtensions
{
    /// <summary>
    /// Maps a sign-out endpoint that ends the person's session in this application and at Sangam,
    /// then returns them to <paramref name="returnUrl"/>. Link to it; do not post to it.
    /// </summary>
    /// <param name="endpoints">The application's endpoints.</param>
    /// <param name="pattern">The path to map.</param>
    /// <param name="returnUrl">A local path to land on after signing out.</param>
    /// <returns>The endpoint's convention builder.</returns>
    public static IEndpointConventionBuilder MapSangamSignOut(this IEndpointRouteBuilder endpoints, string pattern = SangamDefaults.SignOutPath, string returnUrl = "/")
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(returnUrl);
        if (!returnUrl.StartsWith('/') || returnUrl.StartsWith("//", StringComparison.Ordinal))
        {
            throw new ArgumentException("The return address must be a local path such as \"/\".", nameof(returnUrl));
        }

        return endpoints.MapGet(pattern, () => Results.SignOut(
                new AuthenticationProperties { RedirectUri = returnUrl },
                [SangamDefaults.CookieScheme, SangamDefaults.Scheme]))
            .AllowAnonymous();
    }
}
