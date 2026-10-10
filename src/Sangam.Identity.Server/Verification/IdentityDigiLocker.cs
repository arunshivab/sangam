using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Sangam.Identity.Application.Verification;
using Sangam.Identity.Infrastructure.Verification;
using Sangam.Identity.Server.Authentication;

namespace Sangam.Identity.Server.Verification;

/// <summary>
/// rc.6 (SGM-914): the identity server's own DigiLocker round trip, for the two moments that happen during sign-in — an
/// application that requires verification (<see cref="Verify"/>), and recovering an account whose second step is lost
/// (<see cref="Recover"/>). The page that starts it keeps a fresh state and PKCE verifier in a short-lived encrypted
/// cookie bound to the person and the purpose; the callback checks them, exchanges the code, and acts. Nothing but the
/// name, date of birth, gender and DigiLocker id is read, and the id is hashed at once.
/// </summary>
public static class IdentityDigiLocker
{
    /// <summary>The purpose: verify, then continue to the application.</summary>
    public const string Verify = "verify";

    /// <summary>The purpose: recover an account whose second step is lost.</summary>
    public const string Recover = "recover";

    /// <summary>The callback path, under which the cookie lives.</summary>
    public const string CallbackPath = "/identity/digilocker/callback";

    /// <summary>The cookie's base name; <c>__Secure-</c> over HTTPS.</summary>
    public const string CookieName = "sangam.digilocker";

    private const string Protection = "Sangam.Identity.DigiLocker.v1";
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Keeps the round trip's state in the cookie and returns DigiLocker's address to send the person to.
    /// </summary>
    /// <param name="context">The request.</param>
    /// <param name="purpose"><see cref="Verify"/> or <see cref="Recover"/>.</param>
    /// <param name="userId">The person.</param>
    /// <param name="returnUrl">Where to continue afterwards (local only).</param>
    public static string Start(HttpContext context, string purpose, Guid userId, string? returnUrl)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(purpose);
        DigiLockerClient digiLocker = context.RequestServices.GetRequiredService<DigiLockerClient>();
        DigiLockerSettings settings = context.RequestServices.GetRequiredService<DigiLockerSettings>();
        IDataProtectionProvider protection = context.RequestServices.GetRequiredService<IDataProtectionProvider>();

        (string verifier, string challenge) = DigiLockerClient.NewPkce();
        string state = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        Pending pending = new(state, verifier, userId, purpose, Local(returnUrl), DateTimeOffset.UtcNow.Add(Lifetime));
        context.Response.Cookies.Append(CookieNameFor(context), protection.CreateProtector(Protection).Protect(JsonSerializer.Serialize(pending)), new CookieOptions
        {
            HttpOnly = true,
            Secure = context.Request.IsHttps,
            SameSite = SameSiteMode.Lax,
            Path = CallbackPath,
            MaxAge = Lifetime,
            IsEssential = true,
        });
        return digiLocker.AuthorizeUri(state, challenge, RedirectUri(context, settings)).AbsoluteUri;
    }

    /// <summary>Maps the callback.</summary>
    /// <param name="endpoints">The endpoints.</param>
    public static IEndpointRouteBuilder MapIdentityDigiLocker(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        endpoints.MapGet(CallbackPath, CallbackAsync).DisableAntiforgery();
        return endpoints;
    }

    /// <summary>The cookie's name for this request: <c>__Secure-sangam.digilocker</c> over HTTPS.</summary>
    /// <param name="context">The request.</param>
    public static string CookieNameFor(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Request.IsHttps ? "__Secure-" + CookieName : CookieName;
    }

    private static async Task<IResult> CallbackAsync(HttpContext context, DigiLockerClient digiLocker, DigiLockerSettings settings, IDataProtectionProvider protection, IIdentityVerificationService verification, IAccountRecoveryService recovery, CancellationToken cancellationToken)
    {
        Pending? pending = Read(context, protection);
        context.Response.Cookies.Delete(CookieNameFor(context), new CookieOptions { Path = CallbackPath });
        string? state = context.Request.Query["state"];
        string? code = context.Request.Query["code"];
        bool valid = pending is not null && pending.ExpiresAt >= DateTimeOffset.UtcNow && state is not null
            && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(state), Encoding.ASCII.GetBytes(pending.State));
        if (!valid)
        {
            return Results.Redirect(pending?.Purpose == Verify ? Again(pending, "expired") : "/login/recover/result?outcome=expired");
        }

        // Bound to the person: a signed-in session for verification, the pending first step for a recovery.
        Guid? person = pending!.Purpose == Verify
            ? await SessionUserAsync(context).ConfigureAwait(false)
            : (await SangamAuthentication.ReadPendingAsync(context, SangamAuthentication.Pending.Authenticator).ConfigureAwait(false))?.UserId;
        if (person != pending.UserId)
        {
            return Results.Redirect(pending.Purpose == Verify ? Again(pending, "expired") : "/login/recover/result?outcome=expired");
        }

        if (!string.IsNullOrEmpty(context.Request.Query["error"]) || string.IsNullOrEmpty(code))
        {
            return Results.Redirect(pending.Purpose == Verify ? Again(pending, "denied") : "/login/recover/result?outcome=denied");
        }

        VerifiedIdentity? identity;
        try
        {
            identity = await digiLocker.ExchangeAsync(code, pending.Verifier, RedirectUri(context, settings), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            identity = null;
        }

        if (identity is null)
        {
            return Results.Redirect(pending.Purpose == Verify ? Again(pending, "failed") : "/login/recover/result?outcome=failed");
        }

        string? ip = context.Connection.RemoteIpAddress?.ToString();
        if (pending.Purpose == Verify)
        {
            VerificationResult result = await verification.ApplyAsync(pending.UserId, identity, ip, cancellationToken).ConfigureAwait(false);
            return Results.Redirect(result.Succeeded ? pending.ReturnUrl : Again(pending, result.Code));
        }

        RecoveryOutcome outcome = await recovery.StartAsync(pending.UserId, identity, ip, cancellationToken).ConfigureAwait(false);
        await SangamAuthentication.ClearPendingAsync(context).ConfigureAwait(false);
        return Results.Redirect("/login/recover/result?outcome=" + Uri.EscapeDataString(outcome.Code));
    }

    private static async Task<Guid?> SessionUserAsync(HttpContext context)
    {
        AuthenticateResult session = await context.AuthenticateAsync(IdentityConstants.ApplicationScheme).ConfigureAwait(false);
        return session.Succeeded && session.Principal is not null ? SangamAuthentication.UserId(session.Principal) : null;
    }

    private static string Again(Pending? pending, string code)
        => "/identity/required?result=" + Uri.EscapeDataString(code) + "&returnUrl=" + Uri.EscapeDataString(pending?.ReturnUrl ?? "/account");

    private static string RedirectUri(HttpContext context, DigiLockerSettings settings)
        => string.IsNullOrWhiteSpace(settings.IdentityRedirectUri) ? $"{context.Request.Scheme}://{context.Request.Host}{CallbackPath}" : settings.IdentityRedirectUri;

    // A local path only: never another host, never a scheme-relative address.
    private static string Local(string? returnUrl)
        => !string.IsNullOrEmpty(returnUrl) && returnUrl.StartsWith('/') && !returnUrl.StartsWith("//", StringComparison.Ordinal) && !returnUrl.StartsWith("/\\", StringComparison.Ordinal)
            ? returnUrl
            : "/account";

    private static Pending? Read(HttpContext context, IDataProtectionProvider protection)
    {
        if (!context.Request.Cookies.TryGetValue(CookieNameFor(context), out string? value) || string.IsNullOrEmpty(value))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<Pending>(protection.CreateProtector(Protection).Unprotect(value));
        }
        catch (Exception exception) when (exception is CryptographicException or JsonException)
        {
            return null;
        }
    }

    private sealed record Pending(string State, string Verifier, Guid UserId, string Purpose, string ReturnUrl, DateTimeOffset ExpiresAt);
}
