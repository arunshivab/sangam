using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using Sangam.Identity.Infrastructure.Provisioning;
using Sangam.Identity.Infrastructure.Security;

namespace Sangam.Identity.Infrastructure.Tests.Security;

/// <summary>rc.8 (ASVS V9.2.4): outbound TLS checks revocation online, refuses a revoked certificate, and soft-fails only on "unknown".</summary>
public sealed class TlsRevocationTests
{
    [Fact]
    public void ARevokedCertificate_IsRefused_AndAnUncheckableOne_GoesAhead()
    {
        Assert.True(TlsRevocation.OnlyUnknown([]));
        Assert.True(TlsRevocation.OnlyUnknown([Status(X509ChainStatusFlags.RevocationStatusUnknown), Status(X509ChainStatusFlags.OfflineRevocation)]));
        Assert.False(TlsRevocation.OnlyUnknown([Status(X509ChainStatusFlags.Revoked)]));
        Assert.False(TlsRevocation.OnlyUnknown([Status(X509ChainStatusFlags.RevocationStatusUnknown), Status(X509ChainStatusFlags.NotTimeValid)]));
        Assert.False(TlsRevocation.OnlyUnknown([Status(X509ChainStatusFlags.UntrustedRoot)]));

        Assert.True(TlsRevocation.Acceptable(SslPolicyErrors.None, null));
        Assert.False(TlsRevocation.Acceptable(SslPolicyErrors.RemoteCertificateNameMismatch, null));
        Assert.False(TlsRevocation.Acceptable(SslPolicyErrors.RemoteCertificateChainErrors, null));
        Assert.False(TlsRevocation.Acceptable(SslPolicyErrors.RemoteCertificateChainErrors | SslPolicyErrors.RemoteCertificateNameMismatch, null));
    }

    [Fact]
    public void EveryOutboundHandler_ChecksRevocationOnline()
    {
        using SocketsHttpHandler outbound = OutboundHttp.CreateHandler(allowPrivate: false);
        using SocketsHttpHandler plain = TlsRevocation.CreateHandler();
        foreach (SocketsHttpHandler handler in new[] { outbound, plain })
        {
            Assert.Equal(X509RevocationMode.Online, handler.SslOptions.CertificateChainPolicy!.RevocationMode);
            Assert.Equal(TlsRevocation.LookupTimeout, handler.SslOptions.CertificateChainPolicy.UrlRetrievalTimeout);
            Assert.NotNull(handler.SslOptions.RemoteCertificateValidationCallback);
        }
    }

    private static X509ChainStatus Status(X509ChainStatusFlags flag) => new() { Status = flag };
}
