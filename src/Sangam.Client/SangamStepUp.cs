using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Http;
using Sangam.Shared.Constants;

namespace Sangam.Client;

/// <summary>
/// Step-up for applications (PR-17, SGM-207 §4, SGM-306): ask Sangam for a stronger or more recent sign-in before
/// a sensitive action, check that the signed-in person has one, and answer API callers with the RFC 9470 challenge.
/// Sangam owns how strongly people authenticate; your application still decides what they may do.
/// </summary>
public static class SangamStepUp
{
    /// <summary>The authentication property that carries <c>acr_values</c> to Sangam.</summary>
    public const string AcrValuesItem = "sangam.acr_values";

    /// <summary>The assurance level the person signed in at (<c>acr</c>), or <see langword="null"/>.</summary>
    /// <param name="principal">The signed-in person.</param>
    public static string? GetAuthenticationLevel(this ClaimsPrincipal? principal) => principal?.FindFirst("acr")?.Value;

    /// <summary>When the person last authenticated at Sangam (<c>auth_time</c>), or <see langword="null"/>.</summary>
    /// <param name="principal">The signed-in person.</param>
    public static DateTimeOffset? GetAuthenticatedAt(this ClaimsPrincipal? principal)
        => long.TryParse(principal?.FindFirst("auth_time")?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out long seconds)
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : null;

    /// <summary>
    /// Whether the person signed in at <paramref name="level"/> or stronger, and (when <paramref name="maxAge"/> is
    /// given) recently enough. <see cref="SangamAcr.Signature"/> always means within the last five minutes.
    /// </summary>
    /// <param name="principal">The signed-in person.</param>
    /// <param name="level">One of <see cref="SangamAcr"/>.</param>
    /// <param name="maxAge">How recent the authentication must be.</param>
    /// <param name="now">The current time; defaults to now.</param>
    public static bool Satisfies(this ClaimsPrincipal? principal, string level, TimeSpan? maxAge = null, DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(level);
        if (level == SangamAcr.Signature)
        {
            maxAge = maxAge is TimeSpan given && given < TimeSpan.FromMinutes(5) ? given : TimeSpan.FromMinutes(5);
        }

        if (Rank(principal.GetAuthenticationLevel()) < Rank(level))
        {
            return false;
        }

        return maxAge is not TimeSpan limit
            || (principal.GetAuthenticatedAt() is DateTimeOffset at && (now ?? DateTimeOffset.UtcNow) - at <= limit);
    }

    /// <summary>
    /// Sends the person to Sangam to authenticate again at <paramref name="level"/> (and, with
    /// <paramref name="maxAge"/>, freshly), then back to <paramref name="returnUrl"/>.
    /// </summary>
    /// <param name="httpContext">The current request.</param>
    /// <param name="level">One of <see cref="SangamAcr"/>.</param>
    /// <param name="returnUrl">Where to come back to.</param>
    /// <param name="maxAge">How recent the authentication must be; <see langword="null"/> for any.</param>
    public static Task ChallengeAsync(HttpContext httpContext, string level, string returnUrl, TimeSpan? maxAge = null)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(level);
        OpenIdConnectChallengeProperties properties = new() { RedirectUri = returnUrl, MaxAge = maxAge };
        properties.Items[AcrValuesItem] = level;
        return httpContext.ChallengeAsync(SangamDefaults.Scheme, properties);
    }

    /// <summary>
    /// The <c>WWW-Authenticate</c> value for an API that needs a stronger or more recent sign-in (RFC 9470):
    /// answer 401 with it, and the client asks Sangam for a new token at that level.
    /// </summary>
    /// <param name="level">One of <see cref="SangamAcr"/>.</param>
    /// <param name="maxAge">How recent the authentication must be.</param>
    public static string Challenge(string level, TimeSpan? maxAge = null)
    {
        ArgumentNullException.ThrowIfNull(level);
        string header = $"Bearer error=\"insufficient_user_authentication\", error_description=\"A stronger or more recent authentication is required\", acr_values=\"{level}\"";
        return maxAge is TimeSpan age ? header + string.Create(CultureInfo.InvariantCulture, $", max_age={(long)age.TotalSeconds}") : header;
    }

    private static int Rank(string? acr) => acr switch
    {
        SangamAcr.SingleFactor => 1,
        SangamAcr.TwoFactor or SangamAcr.Signature => 2,
        SangamAcr.PhishingResistant => 3,
        _ => 0,
    };
}
