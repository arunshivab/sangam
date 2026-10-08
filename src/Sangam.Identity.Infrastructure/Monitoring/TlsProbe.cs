using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using Sangam.Identity.Application.Monitoring;

namespace Sangam.Identity.Infrastructure.Monitoring;

/// <summary>Reads the certificate each public site presents (D-H: certificate expiry), at most once an hour per site.</summary>
public sealed class TlsProbe
{
    private static readonly TimeSpan Recheck = TimeSpan.FromHours(1);
    private readonly Dictionary<string, (DateTimeOffset At, TlsStatus Status)> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();

    /// <summary>The certificates of <paramref name="hosts"/> (port 443).</summary>
    /// <param name="hosts">Host names.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<IReadOnlyList<TlsStatus>> CheckAsync(IReadOnlyList<string> hosts, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(hosts);
        List<TlsStatus> results = [];
        foreach (string host in hosts)
        {
            lock (_gate)
            {
                if (_cache.TryGetValue(host, out (DateTimeOffset At, TlsStatus Status) cached) && DateTimeOffset.UtcNow - cached.At < Recheck)
                {
                    results.Add(cached.Status);
                    continue;
                }
            }

            TlsStatus status = await ReadAsync(host, cancellationToken).ConfigureAwait(false);
            lock (_gate)
            {
                _cache[host] = (DateTimeOffset.UtcNow, status);
            }

            results.Add(status);
        }

        return results;
    }

    private static async Task<TlsStatus> ReadAsync(string host, CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            using TcpClient tcp = new();
            await tcp.ConnectAsync(host, 443, timeout.Token).ConfigureAwait(false);
            SslStream ssl = new(tcp.GetStream(), leaveInnerStreamOpen: false);
            await using (ssl.ConfigureAwait(false))
            {
                await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = host }, timeout.Token).ConfigureAwait(false);
                using X509Certificate2? certificate = ssl.RemoteCertificate is null ? null : new X509Certificate2(ssl.RemoteCertificate);
                return certificate is null
                    ? new TlsStatus(host, null, "No certificate was presented.")
                    : new TlsStatus(host, new DateTimeOffset(certificate.NotAfter.ToUniversalTime(), TimeSpan.Zero), null);
            }
        }
        catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException or System.Security.Authentication.AuthenticationException)
        {
            return new TlsStatus(host, null, "Could not connect: " + ex.GetType().Name);
        }
    }
}
