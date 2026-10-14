using System.Globalization;
using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Sangam.Identity.Application.Tenancy;

namespace Sangam.Identity.Server.Authorization;

/// <summary>
/// rc.12 (A6; founder decisions, 14 Oct 2026): "Working at which branch?". A person with two or more organisations in an
/// application chooses one at sign-in, and the token carries it. The choice is kept in a protected cookie for that person,
/// on that device, for that application: twelve hours when they tick "Remember on this device", otherwise only for the
/// sign-in in progress. It is never shared with the next person on a ward computer, because it names the person.
/// </summary>
public static class OrgChoice
{
    /// <summary>The prompt value an application sends to ask again (its "Switch organisation").</summary>
    public const string SelectOrgPrompt = "select_org";

    /// <summary>How long a remembered choice lasts.</summary>
    public static readonly TimeSpan Remembered = TimeSpan.FromHours(12);

    /// <summary>How long an unremembered choice lasts: the sign-in in progress.</summary>
    public static readonly TimeSpan ThisSignIn = TimeSpan.FromMinutes(10);

    private const string Purpose = "Sangam.OrgChoice.v1";

    /// <summary>The cookie for one application.</summary>
    /// <param name="appId">The application.</param>
    /// <returns>Its name.</returns>
    public static string CookieName(Guid appId) => "sangam.org." + appId.ToString("N", CultureInfo.InvariantCulture);

    /// <summary>Keeps the person's choice on this device.</summary>
    /// <param name="http">The request.</param>
    /// <param name="protection">Data protection.</param>
    /// <param name="userId">The person.</param>
    /// <param name="appId">The application.</param>
    /// <param name="orgId">The organisation.</param>
    /// <param name="remember">Whether to remember it for twelve hours.</param>
    public static void Write(HttpContext http, IDataProtectionProvider protection, Guid userId, Guid appId, Guid orgId, bool remember)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(protection);
        TimeSpan life = remember ? Remembered : ThisSignIn;
        string value = protection.CreateProtector(Purpose).ToTimeLimitedDataProtector()
            .Protect(string.Join('|', userId.ToString("D"), appId.ToString("D"), orgId.ToString("D")), life);
        http.Response.Cookies.Append(CookieName(appId), value, new CookieOptions
        {
            HttpOnly = true,
            Secure = http.Request.IsHttps,
            SameSite = SameSiteMode.Lax,
            Path = "/",
            MaxAge = life,
            IsEssential = true,
        });
    }

    /// <summary>The organisation this person chose on this device for the application, if the choice is still good.</summary>
    /// <param name="http">The request.</param>
    /// <param name="protection">Data protection.</param>
    /// <param name="userId">The person.</param>
    /// <param name="appId">The application.</param>
    /// <returns>The organisation, or <see langword="null"/>.</returns>
    public static Guid? Read(HttpContext http, IDataProtectionProvider protection, Guid userId, Guid appId)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(protection);
        if (!http.Request.Cookies.TryGetValue(CookieName(appId), out string? value) || string.IsNullOrEmpty(value))
        {
            return null;
        }

        try
        {
            string[] parts = protection.CreateProtector(Purpose).ToTimeLimitedDataProtector().Unprotect(value).Split('|');
            return parts.Length == 3 && Guid.TryParse(parts[0], out Guid user) && user == userId && Guid.TryParse(parts[1], out Guid app) && app == appId && Guid.TryParse(parts[2], out Guid org)
                ? org
                : null;
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    /// <summary>
    /// Which organisation the sign-in is for, without asking: the only one; the one the application named
    /// (<c>sangam_org</c>) when the person works there; or the one chosen on this device. <see langword="null"/> means ask.
    /// </summary>
    /// <param name="orgs">The person's organisations in the application.</param>
    /// <param name="hint">The organisation the application named, if any.</param>
    /// <param name="chosen">The organisation chosen on this device, if any.</param>
    /// <param name="askAgain">Whether the application asked to choose again (<c>prompt=select_org</c>).</param>
    /// <returns>The organisation, or <see langword="null"/>.</returns>
    public static OrgClaim? Resolve(IReadOnlyList<OrgClaim> orgs, Guid? hint, Guid? chosen, bool askAgain)
    {
        ArgumentNullException.ThrowIfNull(orgs);
        if (orgs.Count == 0)
        {
            return null;
        }

        if (orgs.Count == 1)
        {
            return orgs[0];
        }

        if (askAgain)
        {
            return null;
        }

        return orgs.FirstOrDefault(o => o.Id == hint) ?? orgs.FirstOrDefault(o => o.Id == chosen);
    }

    /// <summary>The return address without <c>prompt=select_org</c>, so coming back after choosing does not ask again.</summary>
    /// <param name="url">The return address.</param>
    /// <returns>The address.</returns>
    public static string WithoutSelectPrompt(string url)
    {
        ArgumentNullException.ThrowIfNull(url);
        int q = url.IndexOf('?', StringComparison.Ordinal);
        if (q < 0)
        {
            return url;
        }

        List<string> kept = [];
        foreach (string part in url[(q + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!part.StartsWith("prompt=", StringComparison.OrdinalIgnoreCase))
            {
                kept.Add(part);
                continue;
            }

            string[] values = [.. Uri.UnescapeDataString(part["prompt=".Length..].Replace('+', ' ')).Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(v => v != SelectOrgPrompt)];
            if (values.Length > 0)
            {
                kept.Add("prompt=" + Uri.EscapeDataString(string.Join(' ', values)));
            }
        }

        return url[..q] + (kept.Count == 0 ? string.Empty : "?" + string.Join('&', kept));
    }
}
