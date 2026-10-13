using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.DataProtection;
using Sangam.Identity.Application.Accounts;
using Sangam.Web.Shared.Hosting;

namespace Sangam.Identity.Server.Authentication;

/// <summary>
/// rc.11 (founder decision, 13 Oct 2026): the people who sign in regularly on this browser — the two doctors and three
/// nurses of a ward — so each can tap their name instead of typing. Only people who ticked "Keep me on this device's list"
/// are on it; at most six; anyone who has not signed in here for thirty days drops off; each can be removed with one
/// click. The list shows a first name and a masked email, never roles or the hospital, and lives only in this browser
/// (protected, readable only by the identity server); Sangam keeps no record of who uses which computer. Picking a name
/// only fills in who you are: the password, passkey or code is still asked.
/// </summary>
public static class DeviceAccounts
{
    /// <summary>The cookie's name without its prefix.</summary>
    public const string CookieName = "sangam.accounts";

    /// <summary>The most people on one device's list.</summary>
    public const int MaxPeople = 6;

    /// <summary>How long someone stays on the list without signing in here.</summary>
    public static readonly TimeSpan Unused = TimeSpan.FromDays(30);

    private const string Purpose = "Sangam.Identity.DeviceAccounts.v1";

    /// <summary>The people on this device's list, most recent first, without anyone unused for thirty days.</summary>
    /// <param name="httpContext">Current request.</param>
    public static IReadOnlyList<DeviceAccount> List(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        (string name, IDataProtector protector, _) = Parts(httpContext);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        return [.. Read(protector, httpContext.Request.Cookies[name]).Where(a => now - a.LastUsed < Unused).OrderByDescending(a => a.LastUsed).Take(MaxPeople)];
    }

    /// <summary>
    /// Called when a sign-in completes: puts the person at the top of the list when they asked to be kept on it, or when
    /// they already are on it; otherwise leaves the list as it is.
    /// </summary>
    /// <param name="httpContext">Current request.</param>
    /// <param name="user">The person who signed in.</param>
    /// <param name="remember">Whether they ticked "Keep me on this device's list".</param>
    public static void SignedIn(HttpContext httpContext, UserSummary user, bool remember)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(user);
        List<DeviceAccount> list = [.. List(httpContext)];
        DeviceAccount? existing = list.FirstOrDefault(a => a.UserId == user.Id);
        if (existing is null && !remember)
        {
            return;
        }

        list.RemoveAll(a => a.UserId == user.Id);
        list.Insert(0, new DeviceAccount(existing?.Key ?? NewKey(), user.Id, user.FirstName, MaskEmail(user.Email), DateTimeOffset.UtcNow));
        Write(httpContext, list);
    }

    /// <summary>Removes one person from the list by the key shown with their name.</summary>
    /// <param name="httpContext">Current request.</param>
    /// <param name="key">The entry's key.</param>
    public static void Forget(HttpContext httpContext, string key)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        List<DeviceAccount> list = [.. List(httpContext)];
        if (list.RemoveAll(a => string.Equals(a.Key, key, StringComparison.Ordinal)) > 0)
        {
            Write(httpContext, list);
        }
    }

    /// <summary>Removes a person from the list ("Sign out and forget me here").</summary>
    /// <param name="httpContext">Current request.</param>
    /// <param name="userId">The person.</param>
    public static void ForgetUser(HttpContext httpContext, Guid userId)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        List<DeviceAccount> list = [.. List(httpContext)];
        if (list.RemoveAll(a => a.UserId == userId) > 0)
        {
            Write(httpContext, list);
        }
    }

    /// <summary>An email shown so a colleague recognises it without reading it: <c>ar•••@gmail.com</c>.</summary>
    /// <param name="email">The address.</param>
    public static string MaskEmail(string email)
    {
        ArgumentNullException.ThrowIfNull(email);
        int at = email.IndexOf('@', StringComparison.Ordinal);
        if (at <= 0)
        {
            return "•••";
        }

        string local = email[..at];
        return (local.Length <= 2 ? local[..1] : local[..2]) + "•••" + email[at..];
    }

    private static void Write(HttpContext httpContext, List<DeviceAccount> list)
    {
        (string name, IDataProtector protector, string environment) = Parts(httpContext);
        if (list.Count == 0)
        {
            httpContext.Response.Cookies.Delete(name, new CookieOptions { Path = "/" });
            return;
        }

        httpContext.Response.Cookies.Append(name, protector.Protect(JsonSerializer.Serialize(list.Take(MaxPeople).ToList())), new CookieOptions
        {
            HttpOnly = true,
            Secure = SecurityHeaders.CookiePolicy(environment) == CookieSecurePolicy.Always || httpContext.Request.IsHttps,
            SameSite = SameSiteMode.Lax,
            Path = "/",
            IsEssential = true,
            MaxAge = Unused,
        });
    }

    private static List<DeviceAccount> Read(IDataProtector protector, string? cookie)
    {
        if (string.IsNullOrEmpty(cookie))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<DeviceAccount>>(protector.Unprotect(cookie)) ?? [];
        }
        catch (CryptographicException)
        {
            return [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string NewKey() => Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();

    private static (string Name, IDataProtector Protector, string Environment) Parts(HttpContext httpContext)
    {
        IServiceProvider services = httpContext.RequestServices;
        string environment = services.GetRequiredService<IWebHostEnvironment>().EnvironmentName;
        return (SecurityHeaders.CookieName(CookieName, environment), services.GetRequiredService<IDataProtectionProvider>().CreateProtector(Purpose), environment);
    }
}

/// <summary>One person on this device's list.</summary>
/// <param name="Key">A random key for the entry, used in the page's links instead of the account id.</param>
/// <param name="UserId">The account.</param>
/// <param name="FirstName">First name shown.</param>
/// <param name="MaskedEmail">Masked email shown.</param>
/// <param name="LastUsed">When they last signed in here.</param>
public sealed record DeviceAccount(
    [property: JsonPropertyName("k")] string Key,
    [property: JsonPropertyName("u")] Guid UserId,
    [property: JsonPropertyName("n")] string FirstName,
    [property: JsonPropertyName("m")] string MaskedEmail,
    [property: JsonPropertyName("t")] DateTimeOffset LastUsed);
