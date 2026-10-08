using Sangam.Identity.Domain.Enums;

namespace Sangam.Identity.Application.Signatures;

/// <summary>What an application sends to request a signature (PR-17, SGM-207 §5.1).</summary>
/// <param name="RecordId">The application's id for the record.</param>
/// <param name="RecordHash">The record's hash: <c>sha256:</c>, <c>sha384:</c> or <c>sha512:</c> followed by lowercase hex.</param>
/// <param name="Meaning">What signing means, for example "Approved".</param>
/// <param name="DisplayText">What the person is shown about the record.</param>
/// <param name="ReturnUrl">Where the person goes afterwards; must be one of the application's redirect URIs.</param>
/// <param name="Signer">The only person who may sign (their Sangam subject), or <see langword="null"/> for whoever the application sends.</param>
public sealed record SignatureRequestInput(string RecordId, string RecordHash, string Meaning, string DisplayText, string ReturnUrl, Guid? Signer = null);

/// <summary>A registered request.</summary>
/// <param name="RequestId">Its id.</param>
/// <param name="CeremonyPath">Where to send the person, relative to Sangam's issuer.</param>
/// <param name="ExpiresAt">When it stops being signable.</param>
public sealed record SignatureRequestCreated(Guid RequestId, string CeremonyPath, DateTimeOffset ExpiresAt);

/// <summary>A request as the ceremony shows it.</summary>
/// <param name="Id">Its id.</param>
/// <param name="AppId">The application.</param>
/// <param name="AppName">The application's name.</param>
/// <param name="ClientId">The application's client id (the token's audience).</param>
/// <param name="RecordId">The record's id.</param>
/// <param name="RecordHash">The record's hash.</param>
/// <param name="Meaning">What signing means.</param>
/// <param name="DisplayText">What the person is shown.</param>
/// <param name="Signer">The only person who may sign, if named.</param>
/// <param name="Status">Status.</param>
/// <param name="Expired">Whether a pending request has expired.</param>
/// <param name="ReturnUrl">Where the person goes afterwards.</param>
public sealed record SignatureCeremony(
    Guid Id,
    Guid AppId,
    string AppName,
    string ClientId,
    string RecordId,
    string RecordHash,
    string Meaning,
    string DisplayText,
    Guid? Signer,
    SignatureStatus Status,
    bool Expired,
    string ReturnUrl);

/// <summary>A request as the application reads it back.</summary>
/// <param name="Id">Its id.</param>
/// <param name="Status">"pending", "signed", "declined" or "expired".</param>
/// <param name="Token">The signature token, once signed.</param>
/// <param name="DecidedAt">When it was signed or declined.</param>
/// <param name="DecidedBy">Who signed or declined (their Sangam subject).</param>
public sealed record SignatureView(Guid Id, string Status, string? Token, DateTimeOffset? DecidedAt, Guid? DecidedBy);

/// <summary>The outcome of signing or declining.</summary>
/// <param name="Succeeded">Whether it happened.</param>
/// <param name="Message">What to tell the person.</param>
/// <param name="RedirectUrl">Where to send them, back to the application.</param>
public sealed record SignatureResult(bool Succeeded, string Message, string? RedirectUrl = null);

/// <summary>Everything the signature token binds (SGM-207 §5).</summary>
/// <param name="Issuer">Sangam's issuer.</param>
/// <param name="Audience">The application's client id.</param>
/// <param name="RequestId">The request (<c>jti</c>).</param>
/// <param name="Subject">The signer.</param>
/// <param name="Name">The signer's name, as shown at signing (11.50).</param>
/// <param name="RecordId">The record's id.</param>
/// <param name="RecordHash">The record's hash (11.70).</param>
/// <param name="Meaning">What the signature means (11.50).</param>
/// <param name="SignedAt">When (11.50).</param>
/// <param name="Acr">The assurance level of the authentication.</param>
/// <param name="Methods">The RFC 8176 methods (11.200: two distinct components).</param>
/// <param name="AuthenticatedAt">When the person authenticated for it.</param>
public sealed record SignatureClaims(
    string Issuer,
    string Audience,
    Guid RequestId,
    Guid Subject,
    string Name,
    string RecordId,
    string RecordHash,
    string Meaning,
    DateTimeOffset SignedAt,
    string Acr,
    IReadOnlyList<string> Methods,
    DateTimeOffset AuthenticatedAt);

/// <summary>Signs a signature token with Sangam's token-signing key, so applications verify it with the published keys.</summary>
public interface ISignatureTokenIssuer
{
    /// <summary>Issues the token.</summary>
    /// <param name="claims">What it binds.</param>
    string Issue(SignatureClaims claims);
}

/// <summary>Electronic-signature requests and ceremonies (PR-17, SGM-207 §5).</summary>
public interface ISignatureService
{
    /// <summary>Registers a request. Returns the request, or why it was refused.</summary>
    /// <param name="appId">The calling application.</param>
    /// <param name="input">The request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<(SignatureRequestCreated? Created, string? Error)> CreateAsync(Guid appId, SignatureRequestInput input, CancellationToken cancellationToken = default);

    /// <summary>The request for the ceremony, or <see langword="null"/>.</summary>
    /// <param name="requestId">The request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<SignatureCeremony?> GetAsync(Guid requestId, CancellationToken cancellationToken = default);

    /// <summary>Signs, issuing the token. The caller has checked the authentication is fresh and two-factor.</summary>
    /// <param name="requestId">The request.</param>
    /// <param name="signer">The person.</param>
    /// <param name="signerName">Their name as shown.</param>
    /// <param name="issuer">Sangam's issuer.</param>
    /// <param name="acr">The assurance level of the authentication.</param>
    /// <param name="methods">The RFC 8176 methods.</param>
    /// <param name="authenticatedAt">When the person authenticated.</param>
    /// <param name="ipAddress">The person's IP.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<SignatureResult> SignAsync(Guid requestId, Guid signer, string signerName, string issuer, string acr, IReadOnlyList<string> methods, DateTimeOffset authenticatedAt, string? ipAddress, CancellationToken cancellationToken = default);

    /// <summary>Declines.</summary>
    /// <param name="requestId">The request.</param>
    /// <param name="userId">The person.</param>
    /// <param name="ipAddress">The person's IP.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<SignatureResult> DeclineAsync(Guid requestId, Guid userId, string? ipAddress, CancellationToken cancellationToken = default);

    /// <summary>The request as its application reads it back, or <see langword="null"/> if it is not theirs.</summary>
    /// <param name="appId">The calling application.</param>
    /// <param name="requestId">The request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<SignatureView?> GetForAppAsync(Guid appId, Guid requestId, CancellationToken cancellationToken = default);
}
