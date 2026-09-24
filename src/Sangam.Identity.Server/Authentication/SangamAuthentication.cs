using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Sangam.Identity.Application.Accounts;
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

    /// <summary>Claim on the session cookie: the mode the session was established with.</summary>
    public const string SessionModeClaim = "sangam:signin_mode";

    /// <summary>Claim on the session cookie: the user's security stamp when the session was issued.</summary>
    public const string SessionStampClaim = "sangam:stamp";

    /// <summary>Claim on the session cookie: Unix seconds when the stamp was last checked against the database.</summary>
    public const string SessionValidatedClaim = "sangam:validated";

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
    }

    /// <summary>Registers the session and pending cookie schemes.</summary>
    /// <param name="services">Service collection.</param>
    /// <returns>The same collection.</returns>
    public static IServiceCollection AddSangamCookies(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddAuthentication(IdentityConstants.ApplicationScheme)
            .AddCookie(IdentityConstants.ApplicationScheme, o =>
            {
                o.Cookie.Name = SessionCookieName;
                o.Cookie.HttpOnly = true;
                o.Cookie.SameSite = SameSiteMode.Lax;
                o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
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
                o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
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

    /// <summary>Issues the session cookie for <paramref name="user"/>.</summary>
    /// <param name="httpContext">Current request.</param>
    /// <param name="user">The signed-in user.</param>
    /// <param name="mode">Mode the sign-in used.</param>
    public static async Task SignInSessionAsync(HttpContext httpContext, UserSummary user, SignInMode mode)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(user);

        ClaimsIdentity identity = new(IdentityConstants.ApplicationScheme, Claims.Name, Claims.Role);
        identity.AddClaim(new Claim(Claims.Subject, user.Id.ToString("D")));
        identity.AddClaim(new Claim(Claims.Name, user.DisplayName));
        identity.AddClaim(new Claim(Claims.GivenName, user.FirstName));
        identity.AddClaim(new Claim(Claims.FamilyName, user.LastName));
        identity.AddClaim(new Claim(Claims.Email, user.Email));
        identity.AddClaim(new Claim(Claims.EmailVerified, user.EmailVerified ? "true" : "false"));
        identity.AddClaim(new Claim(SessionModeClaim, SignInModes.ToCode(mode)));
        identity.AddClaim(new Claim(SessionStampClaim, user.SecurityStamp));
        identity.AddClaim(new Claim(SessionValidatedClaim, DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)));
        identity.AddClaim(new Claim(Claims.AuthenticationTime, DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)));

        await httpContext.SignOutAsync(PendingScheme).ConfigureAwait(false);
        await httpContext.SignInAsync(IdentityConstants.ApplicationScheme, new ClaimsPrincipal(identity)).ConfigureAwait(false);
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
    public static Task StorePendingAsync(HttpContext httpContext, string purpose, Guid userId, SignInMode? mode = null)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ClaimsIdentity identity = new(PendingScheme);
        identity.AddClaim(new Claim(Claims.Subject, userId.ToString("D")));
        identity.AddClaim(new Claim(PendingPurposeClaim, purpose));
        if (mode is not null)
        {
            identity.AddClaim(new Claim(PendingModeClaim, SignInModes.ToCode(mode.Value)));
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
        return new PendingFlow(purpose, userId, email, mode);
    }

    /// <summary>Clears the pending flow cookie.</summary>
    /// <param name="httpContext">Current request.</param>
    public static Task ClearPendingAsync(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        return httpContext.SignOutAsync(PendingScheme);
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
public sealed record PendingFlow(string Purpose, Guid? UserId, string? Email, SignInMode? Mode);
