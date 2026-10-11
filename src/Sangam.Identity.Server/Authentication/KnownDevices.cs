using System.Globalization;
using Microsoft.AspNetCore.DataProtection;
using Sangam.Identity.Application.Abstractions;
using Sangam.Identity.Application.Accounts;
using Sangam.Identity.Domain;
using Sangam.Identity.Domain.Enums;
using Sangam.Identity.Infrastructure.Accounts;
using Sangam.Identity.Infrastructure.Portal;
using Sangam.Web.Shared.Hosting;

namespace Sangam.Identity.Server.Authentication;

/// <summary>
/// rc.9 (ASVS V2.2.3; founder decision, 11 Oct 2026): a sign-in from a new device is told to the person by e-mail. A new
/// device is a browser without Sangam's device cookie for that account. The cookie holds, protected, the accounts that have
/// signed in from this browser (the last ten); it is not a tracking id and says nothing to anyone but the identity server.
/// The e-mail names the browser, the network address and the time, and links to the devices page, where the session can be
/// ended. Sangam does not look up where an address is (no location database), so the address is shown as it is.
/// </summary>
public static class KnownDevices
{
    /// <summary>The cookie's name without its prefix.</summary>
    public const string CookieName = "sangam.device";

    /// <summary>How long a browser stays known without signing in.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(400);

    /// <summary>How soon after registering a sign-in is the account's own first (no notice: the person is still there).</summary>
    public static readonly TimeSpan JustRegistered = TimeSpan.FromMinutes(30);

    private const string Purpose = "Sangam.Identity.KnownDevices.v1";
    private const int Remembered = 10;

    /// <summary>
    /// Called when a sign-in completes: tells the person if this browser is new to the account, and remembers it. A notice
    /// that cannot be sent never stops the sign-in.
    /// </summary>
    /// <param name="httpContext">Current request.</param>
    /// <param name="user">The person signing in.</param>
    public static async Task SignedInAsync(HttpContext httpContext, UserSummary user)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(user);
        IServiceProvider services = httpContext.RequestServices;
        string environment = services.GetRequiredService<IWebHostEnvironment>().EnvironmentName;
        string name = SecurityHeaders.CookieName(CookieName, environment);
        IDataProtector protector = services.GetRequiredService<IDataProtectionProvider>().CreateProtector(Purpose);
        List<string> known = Read(protector, httpContext.Request.Cookies[name]);
        string id = user.Id.ToString("N");
        DateTimeOffset now = DateTimeOffset.UtcNow;

        if (!known.Contains(id, StringComparer.Ordinal) && now - user.CreatedAt > JustRegistered)
        {
            string? address = httpContext.Connection.RemoteIpAddress?.ToString();
            string userAgent = httpContext.Request.Headers.UserAgent.ToString();
            IConfiguration configuration = services.GetRequiredService<IConfiguration>();
            string portal = (configuration["Sangam:PortalUrl"] ?? "https://account.sangamid.in").TrimEnd('/');
            await services.GetRequiredService<SecurityNotices>().SendAsync(
                MessageTemplateKinds.NewDeviceSignInNotice,
                user.Id,
                user.Email,
                user.FirstName,
                user.Locale,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["browser"] = UserAgentSummary.Describe(userAgent),
                    ["address"] = address ?? "-",
                    ["time"] = now.ToOffset(Sangam.Shared.IndiaTime.Offset).ToString("d MMM yyyy, HH:mm 'IST'", CultureInfo.InvariantCulture),
                    ["link"] = portal + "/devices",
                },
                httpContext.RequestAborted).ConfigureAwait(false);
            await services.GetRequiredService<IAuditWriter>().WriteAsync(
                new AuditEntry(AuditActions.UserLoginNewDevice, AuditActorType.User, user.Id, TargetType: "user", TargetId: user.Id,
                    IpAddress: address, UserAgent: string.IsNullOrEmpty(userAgent) ? null : userAgent),
                httpContext.RequestAborted).ConfigureAwait(false);
        }

        known.Remove(id);
        known.Insert(0, id);
        httpContext.Response.Cookies.Append(name, protector.Protect(string.Join(',', known.Take(Remembered))), new CookieOptions
        {
            HttpOnly = true,
            Secure = SecurityHeaders.CookiePolicy(environment) == CookieSecurePolicy.Always || httpContext.Request.IsHttps,
            SameSite = SameSiteMode.Lax,
            Path = "/",
            IsEssential = true,
            MaxAge = Lifetime,
        });
    }

    private static List<string> Read(IDataProtector protector, string? cookie)
    {
        if (string.IsNullOrEmpty(cookie))
        {
            return [];
        }

        try
        {
            return [.. protector.Unprotect(cookie).Split(',', StringSplitOptions.RemoveEmptyEntries)];
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            // A cookie from before a key change, or tampered with: the browser counts as new.
            return [];
        }
    }
}
