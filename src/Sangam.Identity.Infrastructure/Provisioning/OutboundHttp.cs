using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Configuration;

namespace Sangam.Identity.Infrastructure.Provisioning;

/// <summary>
/// Calls Sangam makes to addresses a partner typed in — SCIM servers (PR-23) and webhook endpoints (PR-24). A partner
/// must not be able to point Sangam at its own private network (server-side request forgery): the address must be
/// https, redirects are never followed, and the address each connection actually reaches is checked when it is made —
/// after DNS, so a name that later resolves to a private address is caught too. Loopback, private, link-local,
/// carrier-grade NAT, multicast and unspecified addresses are refused unless <c>Sangam:Outbound:AllowPrivateNetworks</c>
/// is set (on by default only in Development and Testing).
/// </summary>
public static class OutboundHttp
{
    /// <summary>The named HTTP client.</summary>
    public const string ClientName = "sangam.outbound";

    /// <summary>The setting that lets Sangam call private addresses (development, tests, an on-premises deployment).</summary>
    public const string AllowPrivateKey = "Sangam:Outbound:AllowPrivateNetworks";

    /// <summary>How long one call may take.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    /// <summary>Whether private addresses are allowed in this environment.</summary>
    /// <param name="configuration">Configuration.</param>
    /// <param name="environmentName">The host environment's name.</param>
    public static bool AllowPrivate(IConfiguration configuration, string environmentName)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        bool development = environmentName is "Development" or "Testing";
        return configuration.GetValue(AllowPrivateKey, development);
    }

    /// <summary>Why an address cannot be used, or <see langword="null"/> when it can.</summary>
    /// <param name="value">The address.</param>
    /// <param name="allowPrivate">Whether private addresses (and plain http to them) are allowed.</param>
    public static string? Check(string? value, bool allowPrivate)
    {
        if (string.IsNullOrWhiteSpace(value) || !Uri.TryCreate(value.Trim(), UriKind.Absolute, out Uri? uri))
        {
            return "Enter the full address, starting with https://.";
        }

        if (uri.Scheme != Uri.UriSchemeHttps && !(allowPrivate && uri.Scheme == Uri.UriSchemeHttp))
        {
            return "The address must start with https://.";
        }

        if (uri.UserInfo.Length > 0 || uri.Fragment.Length > 0)
        {
            return "The address may not contain a user name, a password or a #fragment.";
        }

        if (!allowPrivate && IPAddress.TryParse(uri.Host.Trim('[', ']'), out IPAddress? literal) && IsPrivate(literal))
        {
            return "The address points at a private network, which Sangam does not call.";
        }

        return null;
    }

    /// <summary>The handler behind the named client: no redirects, and every connection's address checked.</summary>
    /// <param name="allowPrivate">Whether private addresses are allowed.</param>
    public static SocketsHttpHandler CreateHandler(bool allowPrivate)
    {
        SocketsHttpHandler handler = new()
        {
            AllowAutoRedirect = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(10),
        };
        if (!allowPrivate)
        {
            handler.ConnectCallback = ConnectPublicAsync;
        }

        return handler;
    }

    /// <summary>Whether an address is one Sangam must not call without <see cref="AllowPrivateKey"/>.</summary>
    /// <param name="address">The address.</param>
    public static bool IsPrivate(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.IPv6None))
        {
            return true;
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            byte[] v6 = address.GetAddressBytes();
            return address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast
                || (v6[0] & 0xFE) == 0xFC; // fc00::/7 unique local
        }

        byte[] b = address.GetAddressBytes();
        return b[0] == 10
            || b[0] == 0
            || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
            || (b[0] == 192 && b[1] == 168)
            || (b[0] == 169 && b[1] == 254)
            || (b[0] == 100 && b[1] >= 64 && b[1] <= 127)
            || b[0] >= 224;
    }

    private static async ValueTask<Stream> ConnectPublicAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        IPAddress[] addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken).ConfigureAwait(false);
        IPAddress? target = addresses.FirstOrDefault(a => !IsPrivate(a))
            ?? throw new HttpRequestException($"{context.DnsEndPoint.Host} resolves only to private addresses, which Sangam does not call.");
        if (addresses.Any(IsPrivate))
        {
            throw new HttpRequestException($"{context.DnsEndPoint.Host} resolves to a private address, which Sangam does not call.");
        }

#pragma warning disable CA2000 // The stream returned owns the socket; it is disposed on failure below.
        Socket socket = new(target.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
#pragma warning restore CA2000
        try
        {
            await socket.ConnectAsync(new IPEndPoint(target, context.DnsEndPoint.Port), cancellationToken).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
