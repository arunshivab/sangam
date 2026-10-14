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

    /// <summary>
    /// rc.12 (A6): maps "Switch organisation". It sends the person back to Sangam to choose which organisation they are
    /// working at (<c>prompt=select_org</c>), then returns them to <paramref name="returnUrl"/> with a new sign-in that
    /// carries that organisation.
    /// </summary>
    /// <param name="endpoints">The application's endpoints.</param>
    /// <param name="pattern">The path to map.</param>
    /// <param name="returnUrl">A local path to land on after choosing.</param>
    /// <returns>The endpoint's convention builder.</returns>
    public static IEndpointConventionBuilder MapSangamSwitchOrganisation(this IEndpointRouteBuilder endpoints, string pattern = SangamDefaults.SwitchOrganisationPath, string returnUrl = "/")
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(returnUrl);
        if (!returnUrl.StartsWith('/') || returnUrl.StartsWith("//", StringComparison.Ordinal))
        {
            throw new ArgumentException("The return address must be a local path such as \"/\".", nameof(returnUrl));
        }

        return endpoints.MapGet(pattern, () =>
        {
            AuthenticationProperties properties = new() { RedirectUri = returnUrl };
            properties.Items[SangamDefaults.PromptItem] = SangamDefaults.SelectOrganisationPrompt;
            return Results.Challenge(properties, [SangamDefaults.Scheme]);
        }).RequireAuthorization();
    }

    /// <summary>
    /// rc.12 (T1): maps "Lock", for when this application's idle timer runs out (point the timer, or a Lock button, at it).
    /// It locks the person's Sangam session with their own access token (needs <see cref="SangamOptions.SaveTokens"/>), ends
    /// this application's own cookie, and sends them back to Sangam: on an enrolled shared ward device they see their tile and
    /// type their PIN; anywhere else they sign in again. Then they land on <paramref name="returnUrl"/>.
    /// </summary>
    /// <param name="endpoints">The application's endpoints.</param>
    /// <param name="pattern">The path to map.</param>
    /// <param name="returnUrl">A local path to land on after unlocking.</param>
    /// <returns>The endpoint's convention builder.</returns>
    public static IEndpointConventionBuilder MapSangamLock(this IEndpointRouteBuilder endpoints, string pattern = SangamDefaults.LockPath, string returnUrl = "/")
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(returnUrl);
        if (!returnUrl.StartsWith('/') || returnUrl.StartsWith("//", StringComparison.Ordinal))
        {
            throw new ArgumentException("The return address must be a local path such as \"/\".", nameof(returnUrl));
        }

        return endpoints.MapGet(pattern, async (HttpContext context, SangamManagementClient sangam) =>
        {
            string? token = await context.GetTokenAsync(SangamDefaults.CookieScheme, "access_token").ConfigureAwait(false);
            if (!string.IsNullOrEmpty(token))
            {
                await sangam.LockSessionAsync(token, context.RequestAborted).ConfigureAwait(false);
            }

            await context.SignOutAsync(SangamDefaults.CookieScheme).ConfigureAwait(false);
            return Results.Challenge(new AuthenticationProperties { RedirectUri = returnUrl }, [SangamDefaults.Scheme]);
        }).RequireAuthorization();
    }

    /// <summary>
    /// rc.12 (M12): maps "Emergency access". Link to it with your own opaque case reference — never the patient's name —
    /// as <c>?case=48213&amp;returnUrl=/cases/48213</c>. Sangam checks it is the person again (a passkey or an authenticator
    /// app), asks for a reason, tells the hospital's reviewers, and returns them with a sign-in whose
    /// <see cref="SangamUser.Emergency"/> is set for up to 60 minutes. End it early with
    /// <see cref="SangamManagementClient.EndEmergencyAsync"/>.
    /// </summary>
    /// <param name="endpoints">The application's endpoints.</param>
    /// <param name="pattern">The path to map.</param>
    /// <returns>The endpoint's convention builder.</returns>
    public static IEndpointConventionBuilder MapSangamEmergency(this IEndpointRouteBuilder endpoints, string pattern = SangamDefaults.EmergencyPath)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(pattern);
        return endpoints.MapGet(pattern, (string? @case, string? returnUrl) =>
        {
            if (string.IsNullOrWhiteSpace(@case) || @case.Length > 64)
            {
                return Results.BadRequest("Give your own case reference as ?case=.");
            }

            string back = !string.IsNullOrEmpty(returnUrl) && returnUrl.StartsWith('/') && !returnUrl.StartsWith("//", StringComparison.Ordinal) && !returnUrl.StartsWith("/\\", StringComparison.Ordinal) ? returnUrl : "/";
            AuthenticationProperties properties = new() { RedirectUri = back };
            properties.Items[SangamDefaults.EmergencyItem] = @case;
            return Results.Challenge(properties, [SangamDefaults.Scheme]);
        }).RequireAuthorization();
    }
}
