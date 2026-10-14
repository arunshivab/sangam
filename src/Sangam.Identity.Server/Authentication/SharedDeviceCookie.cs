using Sangam.Identity.Application.Devices;
using Sangam.Web.Shared.Hosting;

namespace Sangam.Identity.Server.Authentication;

/// <summary>
/// rc.12 (T1): the key of an enrolled shared ward device, kept in that browser only (Sangam keeps its hash). It stays for
/// 400 days, the longest a browser keeps a cookie; an admin removing the device ends it at once.
/// </summary>
public static class SharedDeviceCookie
{
    /// <summary>The cookie's name without its prefix.</summary>
    public const string CookieName = "sangam.shared-device";

    private const string ItemKey = "sangam.shared-device";

    /// <summary>Keeps the device key in this browser.</summary>
    /// <param name="httpContext">Current request.</param>
    /// <param name="key">The device key.</param>
    public static void Write(HttpContext httpContext, string key)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(key);
        string environment = httpContext.RequestServices.GetRequiredService<IWebHostEnvironment>().EnvironmentName;
        httpContext.Response.Cookies.Append(SecurityHeaders.CookieName(CookieName, environment), key, new CookieOptions
        {
            HttpOnly = true,
            Secure = SecurityHeaders.CookiePolicy(environment) == CookieSecurePolicy.Always || httpContext.Request.IsHttps,
            SameSite = SameSiteMode.Lax,
            Path = "/",
            IsEssential = true,
            MaxAge = TimeSpan.FromDays(400),
        });
    }

    /// <summary>The enrolled device this browser is, or <see langword="null"/> (looked up once per request).</summary>
    /// <param name="httpContext">Current request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The device.</returns>
    public static async Task<SharedDeviceInfo?> CurrentAsync(HttpContext httpContext, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        if (httpContext.Items.TryGetValue(ItemKey, out object? cached))
        {
            return cached as SharedDeviceInfo;
        }

        string environment = httpContext.RequestServices.GetRequiredService<IWebHostEnvironment>().EnvironmentName;
        string? key = httpContext.Request.Cookies[SecurityHeaders.CookieName(CookieName, environment)];
        SharedDeviceInfo? device = string.IsNullOrEmpty(key)
            ? null
            : await httpContext.RequestServices.GetRequiredService<ISharedDeviceService>().FindAsync(key, cancellationToken).ConfigureAwait(false);
        httpContext.Items[ItemKey] = device;
        return device;
    }
}
