using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Sangam.Web.Shared.Hosting;

namespace Sangam.Identity.Server.Authentication;

/// <summary>
/// rc.11: what a person typed on the first sign-in screen, carried to the next ones (how to sign in, then the password or
/// the code) in a short-lived, protected cookie, so it is typed once. Nothing in it says whether an account exists: it is
/// only what was typed, whether to keep the person on this device's list, and the first screen's bot-check token.
/// </summary>
public static class SignInFlow
{
    /// <summary>The cookie's name without its prefix.</summary>
    public const string CookieName = "sangam.signin";

    /// <summary>How long the screens after the first one remember what was typed.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(20);

    private const string Purpose = "Sangam.Identity.SignInFlow.v1";

    /// <summary>Remembers what was typed on the first screen.</summary>
    /// <param name="httpContext">Current request.</param>
    /// <param name="start">What to remember.</param>
    public static void Store(HttpContext httpContext, SignInStart start)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(start);
        (string name, ITimeLimitedDataProtector protector, string environment) = Parts(httpContext);
        string value = protector.Protect(JsonSerializer.Serialize(start), Lifetime);
        httpContext.Response.Cookies.Append(name, value, new CookieOptions
        {
            HttpOnly = true,
            Secure = SecurityHeaders.CookiePolicy(environment) == CookieSecurePolicy.Always || httpContext.Request.IsHttps,
            SameSite = SameSiteMode.Lax,
            Path = "/",
            IsEssential = true,
            MaxAge = Lifetime,
        });
    }

    /// <summary>What was typed on the first screen, or <see langword="null"/> when nothing is remembered (or it expired).</summary>
    /// <param name="httpContext">Current request.</param>
    public static SignInStart? Read(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        (string name, ITimeLimitedDataProtector protector, _) = Parts(httpContext);
        string? cookie = httpContext.Request.Cookies[name];
        if (string.IsNullOrEmpty(cookie))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<SignInStart>(protector.Unprotect(cookie));
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Forgets what was typed (the sign-in finished, or the person chose "Not you?").</summary>
    /// <param name="httpContext">Current request.</param>
    public static void Clear(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        (string name, _, _) = Parts(httpContext);
        httpContext.Response.Cookies.Delete(name, new CookieOptions { Path = "/" });
    }

    private static (string Name, ITimeLimitedDataProtector Protector, string Environment) Parts(HttpContext httpContext)
    {
        IServiceProvider services = httpContext.RequestServices;
        string environment = services.GetRequiredService<IWebHostEnvironment>().EnvironmentName;
        return (
            SecurityHeaders.CookieName(CookieName, environment),
            services.GetRequiredService<IDataProtectionProvider>().CreateProtector(Purpose).ToTimeLimitedDataProtector(),
            environment);
    }
}

/// <summary>What the first sign-in screen remembered.</summary>
/// <param name="Identifier">What the person typed: an email, a mobile number or a login ID; or the masked label of the
/// name they picked from this device's list.</param>
/// <param name="Remember">Whether to keep the person on this device's list once they are signed in.</param>
/// <param name="FormToken">The first screen's bot-check token, reused by the code button on the next screen so a quick
/// click is not refused (the person has been on the screens longer than the minimum by then).</param>
/// <param name="PickedUserId">The account picked from this device's list, when the person did not type anything.</param>
public sealed record SignInStart(string Identifier, bool Remember, string? FormToken, Guid? PickedUserId = null);
