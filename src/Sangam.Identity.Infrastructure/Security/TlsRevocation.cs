using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace Sangam.Identity.Infrastructure.Security;

/// <summary>
/// rc.8 (ASVS V9.2.4): every TLS connection Sangam makes — to Anjal, DigiLocker, partners' SCIM and webhook addresses,
/// back-channel logout, the breach check and the SIEM — asks whether the server's certificate has been revoked (OCSP or
/// CRL, online). A certificate reported as revoked is refused. When the answer cannot be had (the responder is down or
/// unreachable), the connection goes ahead: a "soft fail", as browsers do, so an outage at a certificate authority
/// cannot stop sign-in mail. Every other certificate fault (expired, wrong name, untrusted) is refused as before.
/// </summary>
/// <remarks>
/// OCSP and CRL addresses are usually plain http on port 80. If the server's firewall allows only outbound 443, every
/// check soft-fails and the control is in name only; the deployment guide (SGM-605) asks for outbound 80 to be opened.
/// </remarks>
public static class TlsRevocation
{
    /// <summary>How long one revocation lookup may take before it counts as "unknown".</summary>
    public static readonly TimeSpan LookupTimeout = TimeSpan.FromSeconds(5);

    /// <summary>The chain policy: online revocation for the whole chain except the root.</summary>
    /// <returns>A new policy.</returns>
    public static X509ChainPolicy Policy() => new()
    {
        RevocationMode = X509RevocationMode.Online,
        RevocationFlag = X509RevocationFlag.ExcludeRoot,
        UrlRetrievalTimeout = LookupTimeout,
    };

    /// <summary>Whether a server certificate is acceptable: no fault at all, or only "revocation could not be checked".</summary>
    /// <param name="errors">What TLS found.</param>
    /// <param name="chain">The chain built for the certificate.</param>
    /// <returns><see langword="true"/> to go ahead.</returns>
    public static bool Acceptable(SslPolicyErrors errors, X509Chain? chain)
    {
        if (errors == SslPolicyErrors.None)
        {
            return true;
        }

        return errors == SslPolicyErrors.RemoteCertificateChainErrors && chain is not null && OnlyUnknown(chain.ChainStatus);
    }

    /// <summary>Whether a chain's faults are all "revocation unknown" (so the chain is otherwise good).</summary>
    /// <param name="statuses">The chain's statuses.</param>
    /// <returns><see langword="true"/> when nothing worse was found; <see langword="false"/> on revocation or any other fault.</returns>
    public static bool OnlyUnknown(IEnumerable<X509ChainStatus> statuses)
    {
        ArgumentNullException.ThrowIfNull(statuses);
        return statuses.All(s => (s.Status & ~(X509ChainStatusFlags.RevocationStatusUnknown | X509ChainStatusFlags.OfflineRevocation)) == X509ChainStatusFlags.NoError);
    }

    /// <summary>Applies the check to an HTTP handler.</summary>
    /// <param name="handler">The handler.</param>
    /// <returns>The same handler.</returns>
    public static SocketsHttpHandler Apply(SocketsHttpHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        handler.SslOptions.CertificateChainPolicy = Policy();
        handler.SslOptions.RemoteCertificateValidationCallback = (_, _, chain, errors) => Acceptable(errors, chain);
        return handler;
    }

    /// <summary>A handler with the check, for clients that would otherwise use the default one.</summary>
    /// <returns>A new handler.</returns>
    public static SocketsHttpHandler CreateHandler() => Apply(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) });
}
