using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.DataProtection;
using Sangam.Identity.Application.Verification;
using Sangam.Identity.Infrastructure.Verification;
using Sangam.SelfService.Web.Components.Common;

namespace Sangam.SelfService.Web.Verification;

/// <summary>
/// PR-26: the portal's side of DigiLocker verification. The person agrees on the profile page that their name, date of
/// birth and gender will take DigiLocker's values and be locked, and posts here; Sangam sends them to DigiLocker with a
/// fresh state and PKCE verifier, kept in a short-lived encrypted cookie bound to them; DigiLocker sends them back to the
/// callback, which checks the state and the person, exchanges the code, and applies the verification. Every outcome
/// returns to the profile page with a short code it explains.
/// </summary>
public static class DigiLockerEndpoints
{
    /// <summary>The cookie that carries the state across the round trip.</summary>
    public const string CookieName = "sangam.digilocker";

    /// <summary>The callback path, under which the cookie lives.</summary>
    public const string CallbackPath = "/verify/digilocker/callback";

    private const string Purpose = "Sangam.Portal.DigiLocker.v1";
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    /// <summary>Maps the endpoints.</summary>
    /// <param name="app">The application.</param>
    public static void MapDigiLocker(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapPost("/verify/digilocker", async (HttpContext context, IAntiforgery antiforgery, DigiLockerClient digiLocker, DigiLockerSettings settings, IDataProtectionProvider protection, CancellationToken cancellationToken) =>
        {
            if (!await antiforgery.IsRequestValidAsync(context).ConfigureAwait(false))
            {
                return Results.BadRequest();
            }

            IFormCollection form = await context.Request.ReadFormAsync(cancellationToken).ConfigureAwait(false);
            Guid? userId = PortalUser.Id(context.User);
            if (!digiLocker.Enabled || userId is null)
            {
                return Results.Redirect("/profile?digilocker=off");
            }

            if (form["agree"] != "true")
            {
                return Results.Redirect("/profile?digilocker=agree");
            }

            (string verifier, string challenge) = DigiLockerClient.NewPkce();
            string state = Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
            Pending pending = new(state, verifier, userId.Value, DateTimeOffset.UtcNow.Add(Lifetime));
            context.Response.Cookies.Append(CookieName, protection.CreateProtector(Purpose).Protect(JsonSerializer.Serialize(pending)), new CookieOptions
            {
                HttpOnly = true,
                Secure = context.Request.IsHttps,
                SameSite = SameSiteMode.Lax,
                Path = CallbackPath,
                MaxAge = Lifetime,
                IsEssential = true,
            });
            return Results.Redirect(digiLocker.AuthorizeUri(state, challenge, RedirectUri(context, settings)).AbsoluteUri);
        }).RequireAuthorization();

        app.MapGet(CallbackPath, async (HttpContext context, DigiLockerClient digiLocker, DigiLockerSettings settings, IIdentityVerificationService verification, IDataProtectionProvider protection, CancellationToken cancellationToken) =>
        {
            Pending? pending = Read(context, protection);
            context.Response.Cookies.Delete(CookieName, new CookieOptions { Path = CallbackPath });
            Guid? userId = PortalUser.Id(context.User);
            string? state = context.Request.Query["state"];
            string? code = context.Request.Query["code"];
            if (pending is null || userId is null || pending.UserId != userId || pending.ExpiresAt < DateTimeOffset.UtcNow
                || state is null || !System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(state), System.Text.Encoding.ASCII.GetBytes(pending.State)))
            {
                return Results.Redirect("/profile?digilocker=expired");
            }

            if (!string.IsNullOrEmpty(context.Request.Query["error"]) || string.IsNullOrEmpty(code))
            {
                return Results.Redirect("/profile?digilocker=denied");
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
                return Results.Redirect("/profile?digilocker=failed");
            }

            VerificationResult result = await verification.ApplyAsync(userId.Value, identity, context.Connection.RemoteIpAddress?.ToString(), cancellationToken).ConfigureAwait(false);
            return Results.Redirect("/profile?digilocker=" + result.Code);
        }).RequireAuthorization();

        app.MapPost("/verify/digilocker/remove", async (HttpContext context, IAntiforgery antiforgery, IIdentityVerificationService verification, CancellationToken cancellationToken) =>
        {
            if (!await antiforgery.IsRequestValidAsync(context).ConfigureAwait(false))
            {
                return Results.BadRequest();
            }

            Guid? userId = PortalUser.Id(context.User);
            if (userId is null)
            {
                return Results.Redirect("/profile");
            }

            VerificationResult result = await verification.RemoveAsync(userId.Value, context.Connection.RemoteIpAddress?.ToString(), cancellationToken).ConfigureAwait(false);
            return Results.Redirect("/profile?digilocker=" + result.Code);
        }).RequireAuthorization();
    }

    private static string RedirectUri(HttpContext context, DigiLockerSettings settings)
        => string.IsNullOrWhiteSpace(settings.RedirectUri) ? $"{context.Request.Scheme}://{context.Request.Host}{CallbackPath}" : settings.RedirectUri;

    private static Pending? Read(HttpContext context, IDataProtectionProvider protection)
    {
        if (!context.Request.Cookies.TryGetValue(CookieName, out string? value) || string.IsNullOrEmpty(value))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<Pending>(protection.CreateProtector(Purpose).Unprotect(value));
        }
        catch (Exception exception) when (exception is System.Security.Cryptography.CryptographicException or JsonException)
        {
            return null;
        }
    }

    private sealed record Pending(string State, string Verifier, Guid UserId, DateTimeOffset ExpiresAt);
}
