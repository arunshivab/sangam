using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace Sangam.Web.Shared.Hosting;

/// <summary>
/// R7 (ASVS V3.3.2): a console or portal session, however busy, ends a fixed time after the person signed in. The
/// cookie still slides for idle time; this caps the whole session, so the person goes back to the identity server —
/// which has the same cap on its own session — and signs in again.
/// </summary>
public static class SessionLifetime
{
    /// <summary>The claim holding when this session began (Unix seconds).</summary>
    public const string SinceClaim = "sangam_session_since";

    /// <summary>The longest a session lasts, however active.</summary>
    public static readonly TimeSpan Absolute = TimeSpan.FromHours(12);

    /// <summary>Marks a new sign-in with its start time; for <c>OpenIdConnectEvents.OnTicketReceived</c>.</summary>
    /// <param name="context">The sign-in.</param>
    public static Task StampAsync(TicketReceivedContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Principal?.Identity is ClaimsIdentity identity && identity.FindFirst(SinceClaim) is null)
        {
            identity.AddClaim(new Claim(SinceClaim, DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)));
        }

        return Task.CompletedTask;
    }

    /// <summary>Ends a session that is too old, or that carries no start time; for
    /// <c>CookieAuthenticationEvents.OnValidatePrincipal</c>.</summary>
    /// <param name="context">The cookie being checked.</param>
    public static async Task ValidateAsync(CookieValidatePrincipalContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (Expired(context.Principal, DateTimeOffset.UtcNow))
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(context.Scheme.Name).ConfigureAwait(false);
        }
    }

    /// <summary>Whether a session has passed <see cref="Absolute"/>, or has no start time.</summary>
    /// <param name="principal">The session's principal.</param>
    /// <param name="now">The time now.</param>
    public static bool Expired(ClaimsPrincipal? principal, DateTimeOffset now)
    {
        string? since = principal?.FindFirst(SinceClaim)?.Value;
        return !long.TryParse(since, NumberStyles.None, CultureInfo.InvariantCulture, out long seconds)
            || now - DateTimeOffset.FromUnixTimeSeconds(seconds) > Absolute;
    }
}
