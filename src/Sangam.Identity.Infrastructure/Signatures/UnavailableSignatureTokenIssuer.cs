using Sangam.Identity.Application.Signatures;

namespace Sangam.Identity.Infrastructure.Signatures;

/// <summary>
/// The default in hosts that never sign (the portal and the consoles): only the identity server holds the
/// token-signing keys, and it registers the real issuer over this one.
/// </summary>
public sealed class UnavailableSignatureTokenIssuer : ISignatureTokenIssuer
{
    /// <inheritdoc />
    public string Issue(SignatureClaims claims)
        => throw new InvalidOperationException("Signature tokens are issued only by the identity server.");
}
