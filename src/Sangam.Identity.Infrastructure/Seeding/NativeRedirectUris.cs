using System.Net;

namespace Sangam.Identity.Infrastructure.Seeding;

/// <summary>
/// The redirect addresses a native application may register (RFC 8252 §7, SGM-219): a claimed https address (App
/// Links, Universal Links); a private-use scheme in reverse-domain form (<c>in.sangamid.app:/callback</c>); or the
/// loopback interface by IP literal for desktop applications (<c>http://127.0.0.1/callback</c>, any port at sign-in).
/// Never <c>localhost</c> by name, never a fragment, never a web scheme other than those.
/// </summary>
public static class NativeRedirectUris
{
    /// <summary>Why an address cannot be a native redirect address, or <see langword="null"/> when it can.</summary>
    /// <param name="value">The address.</param>
    public static string? Validate(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri))
        {
            return $"'{value}' is not an absolute address.";
        }

        if (uri.Fragment.Length > 0)
        {
            return $"'{value}' has a fragment; redirect addresses may not.";
        }

        if (uri.Scheme == Uri.UriSchemeHttps)
        {
            return uri.IsLoopback ? $"'{value}': loopback addresses use http (RFC 8252 §7.3)." : null;
        }

        if (uri.Scheme == Uri.UriSchemeHttp)
        {
            // §7.3: loopback by IP literal; "localhost" can be redirected by a hosts file or firewall.
            return IPAddress.TryParse(uri.Host.Trim('[', ']'), out IPAddress? ip) && IPAddress.IsLoopback(ip)
                ? null
                : $"'{value}': plain http is allowed only to the loopback interface by IP literal (127.0.0.1 or [::1]).";
        }

        // §7.1: a private-use scheme must be in reverse-domain form, so it cannot collide with another app's.
        return uri.Scheme.Contains('.', StringComparison.Ordinal)
            ? null
            : $"'{value}': a private-use scheme must be a reverse domain name the developer controls, such as in.sangamid.app.";
    }
}
