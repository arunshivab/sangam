using System.Globalization;
using System.Security.Claims;

namespace Sangam.Web.Shared.Security;

/// <summary>
/// rc.9 (ASVS V3.7.1, V3.3.4; founder decision 1, 11 Oct 2026): a sensitive change — password, e-mail, mobile, sign-in
/// method, authenticator, passkeys, recovery codes, ending sessions, exporting or deleting the account — needs a sign-in
/// from the last 15 minutes. The time is the OpenID Connect <c>auth_time</c> claim, which the identity server sets when the
/// person actually authenticates (not when a session is merely reused).
/// </summary>
public static class RecentSignIn
{
    /// <summary>How long a sign-in counts as recent (the same window as merging two accounts).</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(15);

    /// <summary>The claim that carries the authentication time, in seconds since 1970.</summary>
    public const string Claim = "auth_time";

    /// <summary>When the person last authenticated, or <see langword="null"/> when the principal does not say.</summary>
    /// <param name="principal">The signed-in person.</param>
    /// <returns>The time, or <see langword="null"/>.</returns>
    public static DateTimeOffset? At(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        return long.TryParse(principal.FindFirstValue(Claim), NumberStyles.None, CultureInfo.InvariantCulture, out long seconds)
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : null;
    }

    /// <summary>Whether the person authenticated within <see cref="Window"/> of <paramref name="now"/>.</summary>
    /// <param name="principal">The signed-in person.</param>
    /// <param name="now">The current time.</param>
    /// <returns><see langword="true"/> when the sign-in is recent; <see langword="false"/> when it is older or unknown.</returns>
    public static bool IsRecent(ClaimsPrincipal principal, DateTimeOffset now)
        => At(principal) is DateTimeOffset at && now - at <= Window && at <= now.AddMinutes(1);

    /// <summary>A safe local return address: a path on this site, never another site (<c>//host</c> or a scheme).</summary>
    /// <param name="returnUrl">The address asked for.</param>
    /// <param name="fallback">Where to go when it is not safe.</param>
    /// <returns>The address to use.</returns>
    public static string LocalReturn(string? returnUrl, string fallback = "/")
        => returnUrl is { Length: > 0 } r && r[0] == '/' && (r.Length == 1 || (r[1] != '/' && r[1] != '\\')) ? r : fallback;
}
