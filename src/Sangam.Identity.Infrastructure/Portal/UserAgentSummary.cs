namespace Sangam.Identity.Infrastructure.Portal;

/// <summary>
/// Turns a user-agent string into a short "Chrome on Windows" label. Deliberately crude:
/// it recognises the common families and says "Unknown browser" otherwise rather than
/// pretending to know. No parsing library, no network lookup, no guessed location.
/// </summary>
public static class UserAgentSummary
{
    /// <summary>Summarises <paramref name="userAgent"/>.</summary>
    /// <param name="userAgent">Raw user agent, possibly <see langword="null"/>.</param>
    /// <returns>Something like "Chrome on Windows", or "Unknown browser".</returns>
    public static string Describe(string? userAgent)
    {
        if (string.IsNullOrWhiteSpace(userAgent))
        {
            return "Unknown browser";
        }

        string browser = Browser(userAgent);
        string platform = Platform(userAgent);
        return platform.Length == 0 ? browser : browser + " on " + platform;
    }

    private static string Browser(string ua)
    {
        if (Has(ua, "Edg/"))
        {
            return "Edge";
        }

        if (Has(ua, "OPR/") || Has(ua, "Opera"))
        {
            return "Opera";
        }

        if (Has(ua, "SamsungBrowser"))
        {
            return "Samsung Internet";
        }

        if (Has(ua, "Firefox"))
        {
            return "Firefox";
        }

        if (Has(ua, "Chrome") || Has(ua, "CriOS"))
        {
            return "Chrome";
        }

        if (Has(ua, "Safari"))
        {
            return "Safari";
        }

        return "Unknown browser";
    }

    private static string Platform(string ua)
    {
        if (Has(ua, "Android"))
        {
            return "Android";
        }

        if (Has(ua, "iPhone") || Has(ua, "iPad") || Has(ua, "iOS"))
        {
            return "iOS";
        }

        if (Has(ua, "Windows"))
        {
            return "Windows";
        }

        if (Has(ua, "Mac OS X") || Has(ua, "Macintosh"))
        {
            return "macOS";
        }

        if (Has(ua, "CrOS"))
        {
            return "ChromeOS";
        }

        if (Has(ua, "Linux"))
        {
            return "Linux";
        }

        return string.Empty;
    }

    private static bool Has(string ua, string token) => ua.Contains(token, StringComparison.OrdinalIgnoreCase);
}
