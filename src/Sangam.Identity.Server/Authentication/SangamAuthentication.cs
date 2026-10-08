using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Sangam.Identity.Application.Accounts;
using Sangam.Identity.Application.Portal;
using Sangam.Identity.Domain;
using Sangam.Identity.Domain.Enums;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Sangam.Identity.Server.Authentication;

/// <summary>
/// The two cookies id.sangamid.in uses: the session cookie for signed-in users, and a short-lived
/// "pending" cookie that carries a half-finished flow (registered but not verified, password
/// accepted but code outstanding, reset requested) between server-rendered steps without putting
/// identifiers in the URL.
/// </summary>
public static class SangamAuthentication
{
    /// <summary>Scheme of the pending-flow cookie.</summary>
    public const string PendingScheme = "sangam.pending";

    /// <summary>Name of the session cookie.</summary>
    public const string SessionCookieName = "sangam.session";

    /// <summary>Name of the pending-flow cookie.</summary>
    public const string PendingCookieName = "sangam.pending";

    /// <summary>Claim carrying the pending flow's purpose.</summary>
    public const string PendingPurposeClaim = "sangam:pending";

    /// <summary>Claim carrying the sign-in mode of a pending sign-in.</summary>
    public const string PendingModeClaim = "sangam:mode";

    /// <summary>Claim carrying the email address of a pending password reset.</summary>
    public const string PendingEmailClaim = "sangam:email";

    /// <summary>Claim on the session: the person passed an authenticator step (PR-16); a passkey session needs none.</summary>
    public const string SessionSecondFactorClaim = "sangam:second_factor";

    /// <summary>Claim on the session: the RFC 8176 methods used, space-separated (PR-17).</summary>
    public const string SessionMethodsClaim = "sangam:amr";

    /// <summary>Claim on a concealed pending registration: when its notice was sent, for the countdown (V-09).</summary>
    public const string PendingIssuedClaim = "sangam:issued";

    /// <summary>Claim marking a pending code sign-in whose code was texted rather than e-mailed (PR-15).</summary>
    public const string PendingChannelClaim = "sangam:channel";

    /// <summary>Claim on the session cookie: the mode the session was established with.</summary>
    public const string SessionModeClaim = "sangam:signin_mode";

    /// <summary>Claim on the session cookie: the user's security stamp when the session was issued.</summary>
    public const string SessionStampClaim = "sangam:stamp";

    /// <summary>Claim on the session cookie: Unix seconds when the stamp was last checked against the database.</summary>
    public const string SessionValidatedClaim = "sangam:validated";

    /// <summary>Claim on the session cookie: the <c>user_sessions</c> row this cookie belongs to.</summary>
    public const string SessionIdClaim = "sangam:sid";

    /// <summary>How often a session's stamp is re-checked against the database.</summary>
    public static readonly TimeSpan ValidationInterval = TimeSpan.FromMinutes(5);

    /// <summary>Pending-flow purposes.</summary>
    public static class Pending
    {
        /// <summary>Account created, email not yet verified.</summary>
        public const string EmailVerification = "email-verification";

        /// <summary>Credentials accepted, one-time code outstanding.</summary>
        public const string SignInOtp = "sign-in-otp";

        /// <summary>Password reset requested.</summary>
        public const string PasswordReset = "password-reset";

        /// <summary>First factor accepted; an authenticator code is still needed.</summary>
        public const string Authenticator = "authenticator";

        /// <summary>The user declined consent for an app (the app's client id travels in the email slot).</summary>
        public const string ConsentDenied = "consent-denied";

        /// <summary>The password was right but a policy requires a new one before the sign-in continues (PR-16).</summary>
        public const string PasswordUpgrade = "password-upgrade";
    }

    /// <summary>Registers the session and pending cookie schemes.</summary>
    /// <param name="services">Service collection.</param>
    /// <param name="environmentName">The host environment: cookies are always Secure outside Development and Testing (R7).</param>
    /// <returns>The same collection.</returns>
    public static IServiceCollection AddSangamCookies(this IServiceCollection services, string environmentName)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(environmentName);
        CookieSecurePolicy secure = Sangam.Web.Shared.Hosting.SecurityHeaders.CookiePolicy(environmentName);

        services.AddAuthentication(IdentityConstants.ApplicationScheme)
            .AddCookie(IdentityConstants.ApplicationScheme, o =>
            {
                o.Cookie.Name = SessionCookieName;
                o.Cookie.HttpOnly = true;
                o.Cookie.SameSite = SameSiteMode.Lax;
                o.Cookie.SecurePolicy = secure;
                o.LoginPath = "/login";
                o.LogoutPath = "/logout";
                o.AccessDeniedPath = "/login";
                o.SlidingExpiration = true;
                o.ExpireTimeSpan = TimeSpan.FromHours(12);
                o.Events.OnValidatePrincipal = ValidateSessionAsync;
            })
            .AddCookie(PendingScheme, o =>
            {
                o.Cookie.Name = PendingCookieName;
                o.Cookie.HttpOnly = true;
                o.Cookie.SameSite = SameSiteMode.Lax;
                o.Cookie.SecurePolicy = secure;
                o.SlidingExpiration = false;
                o.ExpireTimeSpan = TimeSpan.FromMinutes(15);
                // Never redirect: a stale pending cookie just means "start again".
                o.Events.OnRedirectToLogin = ctx =>
                {
                    ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return Task.CompletedTask;
                };
            });

        return services;
    }

    /// <summary>Issues the session cookie for <paramref name="user"/> and records the session row.</summary>
    /// <param name="httpContext">Current request.</param>
    /// <param name="user">The signed-in user.</param>
    /// <param name="mode">Mode the sign-in used.</param>
    /// <param name="appId">The app that started the flow, if any.</param>
    /// <param name="deviceLabel">Label the app supplied on the authorization request, if any.</param>
    /// <param name="secondFactor">Whether the person passed an authenticator step (PR-16).</param>
    /// <param name="codeBySms">Whether the sign-in code was texted (PR-15), for the methods recorded (PR-17).</param>
    public static async Task SignInSessionAsync(HttpContext httpContext, UserSummary user, SignInMode mode, Guid? appId = null, string? deviceLabel = null, bool secondFactor = false, bool codeBySms = false)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(user);

        ISessionService sessions = httpContext.RequestServices.GetRequiredService<ISessionService>();
        string? userAgent = httpContext.Request.Headers.UserAgent.ToString();
        Guid sessionId = await sessions.StartAsync(
            user.Id,
            mode,
            appId,
            deviceLabel,
            httpContext.Connection.RemoteIpAddress?.ToString(),
            string.IsNullOrEmpty(userAgent) ? null : userAgent,
            httpContext.RequestAborted).ConfigureAwait(false);

        ClaimsIdentity identity = new(IdentityConstants.ApplicationScheme, Claims.Name, Claims.Role);
        identity.AddClaim(new Claim(Claims.Subject, user.Id.ToString("D")));
        identity.AddClaim(new Claim(Claims.Name, user.DisplayName));
        identity.AddClaim(new Claim(Claims.GivenName, user.FirstName));
        identity.AddClaim(new Claim(Claims.FamilyName, user.LastName));
        identity.AddClaim(new Claim(Claims.Email, user.Email));
        identity.AddClaim(new Claim(Claims.EmailVerified, user.EmailVerified ? "true" : "false"));
        identity.AddClaim(new Claim(Claims.Locale, user.Locale));
        identity.AddClaim(new Claim(SessionModeClaim, SignInModes.ToCode(mode)));
        identity.AddClaim(new Claim(SessionStampClaim, user.SecurityStamp));
        identity.AddClaim(new Claim(SessionIdClaim, sessionId.ToString("D")));
        identity.AddClaim(new Claim(SessionValidatedClaim, DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)));
        identity.AddClaim(new Claim(Claims.AuthenticationTime, DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)));
        if (secondFactor)
        {
            identity.AddClaim(new Claim(SessionSecondFactorClaim, "totp"));
        }

        // PR-17: how the person authenticated (RFC 8176), for acr, amr and step-up decisions.
        identity.AddClaim(new Claim(SessionMethodsClaim, string.Join(' ', AuthenticationAssurance.Methods(mode, secondFactor, codeBySms))));

        await httpContext.SignOutAsync(PendingScheme).ConfigureAwait(false);
        await httpContext.SignInAsync(IdentityConstants.ApplicationScheme, new ClaimsPrincipal(identity)).ConfigureAwait(false);
    }

    /// <summary>
    /// Keeps this device signed in after the person changed their own password (R7): the cookie is issued again with
    /// the new security stamp, the same session and the same sign-in methods. Every other device still carries the old
    /// stamp and is signed out at its next check.
    /// </summary>
    /// <param name="httpContext">Current request.</param>
    /// <param name="securityStamp">The person's new security stamp.</param>
    public static async Task RenewStampAsync(HttpContext httpContext, string securityStamp)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(securityStamp);
        AuthenticateResult current = await httpContext.AuthenticateAsync(IdentityConstants.ApplicationScheme).ConfigureAwait(false);
        if (current.Principal?.Identity is not ClaimsIdentity identity)
        {
            return;
        }

        foreach (Claim old in identity.FindAll(c => c.Type is SessionStampClaim or SessionValidatedClaim).ToList())
        {
            identity.RemoveClaim(old);
        }

        identity.AddClaim(new Claim(SessionStampClaim, securityStamp));
        identity.AddClaim(new Claim(SessionValidatedClaim, DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)));
        await httpContext.SignInAsync(IdentityConstants.ApplicationScheme, current.Principal, current.Properties).ConfigureAwait(false);
    }

    /// <summary>
    /// Every <see cref="ValidationInterval"/>, compares the session's stamp with the database.
    /// A password reset or a forced sign-out rotates the stamp, so every other device's session
    /// ends within the interval — the "signs you out everywhere" promise.
    /// </summary>
    private static async Task ValidateSessionAsync(CookieValidatePrincipalContext context)
    {
        ClaimsPrincipal? principal = context.Principal;
        Guid? userId = principal is null ? null : UserId(principal);
        string? stamp = principal?.FindFirstValue(SessionStampClaim);
        long validated = long.TryParse(principal?.FindFirstValue(SessionValidatedClaim), NumberStyles.None, CultureInfo.InvariantCulture, out long v) ? v : 0;

        if (principal is null || userId is null || stamp is null)
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(IdentityConstants.ApplicationScheme).ConfigureAwait(false);
            return;
        }

        // R7 (ASVS V3.3.2): however busy, a session ends a fixed time after the person signed in.
        long signedIn = long.TryParse(principal.FindFirstValue(Claims.AuthenticationTime), NumberStyles.None, CultureInfo.InvariantCulture, out long t) ? t : 0;
        if (DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(signedIn) > Sangam.Web.Shared.Hosting.SessionLifetime.Absolute)
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(IdentityConstants.ApplicationScheme).ConfigureAwait(false);
            return;
        }

        if (DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(validated) < ValidationInterval)
        {
            return;
        }

        IAccountService accounts = context.HttpContext.RequestServices.GetRequiredService<IAccountService>();
        UserSummary? user = await accounts.FindByIdAsync(userId.Value).ConfigureAwait(false);
        if (user is null || !string.Equals(user.SecurityStamp, stamp, StringComparison.Ordinal))
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(IdentityConstants.ApplicationScheme).ConfigureAwait(false);
            return;
        }

        // The session row is the per-device switch: revoking it here ends only this cookie.
        Guid? sessionId = SessionId(principal);
        if (sessionId is not null)
        {
            ISessionService sessions = context.HttpContext.RequestServices.GetRequiredService<ISessionService>();
            if (!await sessions.TouchAsync(sessionId.Value, userId.Value).ConfigureAwait(false))
            {
                context.RejectPrincipal();
                await context.HttpContext.SignOutAsync(IdentityConstants.ApplicationScheme).ConfigureAwait(false);
                return;
            }
        }

        ClaimsIdentity identity = (ClaimsIdentity)principal.Identity!;
        Claim? old = identity.FindFirst(SessionValidatedClaim);
        if (old is not null)
        {
            identity.RemoveClaim(old);
        }

        identity.AddClaim(new Claim(SessionValidatedClaim, DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)));
        context.ShouldRenew = true;
    }

    /// <summary>Stores a pending flow for a known user.</summary>
    /// <param name="httpContext">Current request.</param>
    /// <param name="purpose">One of <see cref="Pending"/>.</param>
    /// <param name="userId">The user.</param>
    /// <param name="mode">Sign-in mode, for <see cref="Pending.SignInOtp"/>.</param>
    /// <param name="viaSms">For <see cref="Pending.SignInOtp"/>: the code was texted, not e-mailed.</param>
    public static Task StorePendingAsync(HttpContext httpContext, string purpose, Guid userId, SignInMode? mode = null, bool viaSms = false)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ClaimsIdentity identity = new(PendingScheme);
        identity.AddClaim(new Claim(Claims.Subject, userId.ToString("D")));
        identity.AddClaim(new Claim(PendingPurposeClaim, purpose));
        if (mode is not null)
        {
            identity.AddClaim(new Claim(PendingModeClaim, SignInModes.ToCode(mode.Value)));
        }

        if (viaSms)
        {
            identity.AddClaim(new Claim(PendingChannelClaim, "sms"));
        }

        return httpContext.SignInAsync(PendingScheme, new ClaimsPrincipal(identity));
    }

    /// <summary>Stores a pending password reset for an email address (the address may or may not exist; the cookie does not say).</summary>
    /// <param name="httpContext">Current request.</param>
    /// <param name="email">The address the user typed.</param>
    public static Task StorePendingResetAsync(HttpContext httpContext, string email)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ClaimsIdentity identity = new(PendingScheme);
        identity.AddClaim(new Claim(PendingPurposeClaim, Pending.PasswordReset));
        identity.AddClaim(new Claim(PendingEmailClaim, email));
        return httpContext.SignInAsync(PendingScheme, new ClaimsPrincipal(identity));
    }

    /// <summary>
    /// Stores a pending e-mail verification for a registration that was concealed because the address or mobile
    /// already had an account (V-09). The next screen looks exactly like a real one; no code will ever match.
    /// </summary>
    /// <param name="httpContext">Current request.</param>
    /// <param name="email">The address that was typed.</param>
    public static Task StorePendingConcealedRegistrationAsync(HttpContext httpContext, string email)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ClaimsIdentity identity = new(PendingScheme);
        identity.AddClaim(new Claim(Claims.Subject, Guid.Empty.ToString("D")));
        identity.AddClaim(new Claim(PendingPurposeClaim, Pending.EmailVerification));
        identity.AddClaim(new Claim(PendingEmailClaim, email));
        identity.AddClaim(new Claim(PendingIssuedClaim, DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)));
        return httpContext.SignInAsync(PendingScheme, new ClaimsPrincipal(identity));
    }

    /// <summary>When the (concealed) code was "sent", for the resend countdown (V-09).</summary>
    /// <param name="httpContext">Current request.</param>
    public static async Task<DateTimeOffset?> ReadPendingIssuedAsync(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        AuthenticateResult result = await httpContext.AuthenticateAsync(PendingScheme).ConfigureAwait(false);
        return long.TryParse(result.Principal?.FindFirstValue(PendingIssuedClaim), NumberStyles.None, CultureInfo.InvariantCulture, out long seconds)
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : null;
    }

    /// <summary>Parks a consent denial for <paramref name="clientId"/> so the authorization endpoint can answer the app.</summary>
    /// <param name="httpContext">Current request.</param>
    /// <param name="clientId">The app that was declined.</param>
    public static Task StorePendingConsentDeniedAsync(HttpContext httpContext, string clientId)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ClaimsIdentity identity = new(PendingScheme);
        identity.AddClaim(new Claim(PendingPurposeClaim, Pending.ConsentDenied));
        identity.AddClaim(new Claim(PendingEmailClaim, clientId));
        return httpContext.SignInAsync(PendingScheme, new ClaimsPrincipal(identity));
    }

    /// <summary>Reads the pending flow, or <see langword="null"/> when none (or the wrong one) is present.</summary>
    /// <param name="httpContext">Current request.</param>
    /// <param name="purpose">Expected purpose.</param>
    public static async Task<PendingFlow?> ReadPendingAsync(HttpContext httpContext, string purpose)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        AuthenticateResult result = await httpContext.AuthenticateAsync(PendingScheme).ConfigureAwait(false);
        if (!result.Succeeded || result.Principal is null)
        {
            return null;
        }

        if (result.Principal.FindFirstValue(PendingPurposeClaim) != purpose)
        {
            return null;
        }

        Guid? userId = Guid.TryParse(result.Principal.FindFirstValue(Claims.Subject), out Guid id) ? id : null;
        string? email = result.Principal.FindFirstValue(PendingEmailClaim);
        SignInMode? mode = SignInModes.TryParse(result.Principal.FindFirstValue(PendingModeClaim), out SignInMode m) ? m : null;
        bool viaSms = result.Principal.FindFirstValue(PendingChannelClaim) == "sms";
        return new PendingFlow(purpose, userId, email, mode, viaSms);
    }

    /// <summary>Clears the pending flow cookie.</summary>
    /// <param name="httpContext">Current request.</param>
    public static Task ClearPendingAsync(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        return httpContext.SignOutAsync(PendingScheme);
    }

    /// <summary>Whether the session meets a second-factor requirement: an authenticator step, or a passkey (PR-16).</summary>
    /// <param name="principal">The session principal.</param>
    public static bool HasSecondFactor(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        return principal.HasClaim(c => c.Type == SessionSecondFactorClaim)
            || principal.FindFirstValue(SessionModeClaim) == SignInModes.ToCode(SignInMode.Passkey);
    }

    /// <summary>The RFC 8176 methods the session was established with (PR-17); empty for a session from before R3.</summary>
    /// <param name="principal">The session principal.</param>
    public static IReadOnlyList<string> SessionMethods(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        string? value = principal.FindFirstValue(SessionMethodsClaim);
        return string.IsNullOrWhiteSpace(value) ? [] : value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>When the person last authenticated in this session, or <see langword="null"/>.</summary>
    /// <param name="principal">The session principal.</param>
    public static DateTimeOffset? AuthenticatedAt(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        return long.TryParse(principal.FindFirstValue(Claims.AuthenticationTime), NumberStyles.None, CultureInfo.InvariantCulture, out long seconds)
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : null;
    }

    /// <summary>The session row id from the cookie, or <see langword="null"/>.</summary>
    /// <param name="principal">The request principal.</param>
    public static Guid? SessionId(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        return Guid.TryParse(principal.FindFirstValue(SessionIdClaim), out Guid id) ? id : null;
    }

    /// <summary>Signed-in user id from the session cookie, or <see langword="null"/>.</summary>
    /// <param name="principal">The request principal.</param>
    public static Guid? UserId(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        return Guid.TryParse(principal.FindFirstValue(Claims.Subject), out Guid id) ? id : null;
    }
}

/// <summary>A half-finished flow carried by the pending cookie.</summary>
/// <param name="Purpose">One of <see cref="SangamAuthentication.Pending"/>.</param>
/// <param name="UserId">The user, when known.</param>
/// <param name="Email">The typed email, for password reset.</param>
/// <param name="Mode">Sign-in mode, for a pending sign-in.</param>
/// <param name="ViaSms">For a pending code sign-in: the code was texted rather than e-mailed.</param>
public sealed record PendingFlow(string Purpose, Guid? UserId, string? Email, SignInMode? Mode, bool ViaSms = false);
