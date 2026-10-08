namespace Sangam.Identity.Domain.Entities;

/// <summary>
/// A SAML 2.0 service provider (PR-22, SGM-215): an application that speaks only SAML and signs people in with
/// SangamID, Sangam being its identity provider. Linked to an <see cref="App"/> row, so consent, branding, the
/// portal's connected apps and the audit log treat it like any other application.
/// </summary>
public class SamlServiceProvider
{
    /// <summary>Row id.</summary>
    public Guid Id { get; set; }

    /// <summary>The application row (its client id starts <c>saml-</c>).</summary>
    public Guid AppId { get; set; }

    /// <summary>The SP's entity id, exactly as its requests name it.</summary>
    public string EntityId { get; set; } = string.Empty;

    /// <summary>Assertion consumer service addresses (HTTP-POST), one per line; the first is the default.</summary>
    public string AcsUrls { get; set; } = string.Empty;

    /// <summary>Single logout address (HTTP-Redirect), if the SP has one.</summary>
    public string? SloUrl { get; set; }

    /// <summary>The SP's signing certificate (PEM), to check signed requests.</summary>
    public string? SigningCertificate { get; set; }

    /// <summary>The SP's encryption certificate (PEM): when set, assertions are encrypted for it.</summary>
    public string? EncryptionCertificate { get; set; }

    /// <summary><c>persistent</c> (pairwise per SP, the default) or <c>email</c>.</summary>
    public string NameIdFormat { get; set; } = "persistent";

    /// <summary>Attributes released, comma-separated: <c>name</c>, <c>given_name</c>, <c>family_name</c>, <c>email</c>, <c>roles</c>, <c>orgs</c>.</summary>
    public string Attributes { get; set; } = "name,email";

    /// <summary>Whether its requests must be signed.</summary>
    public bool RequireSignedRequests { get; set; }

    /// <summary>Whether people may start at Sangam (IdP-initiated); off by default, weaker against CSRF.</summary>
    public bool AllowIdpInitiated { get; set; }

    /// <summary>Where an IdP-initiated sign-in lands (sent as RelayState), if the SP wants one.</summary>
    public string? DefaultRelayState { get; set; }

    /// <summary>When registered.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>When last changed.</summary>
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// An AuthnRequest being answered (PR-22): kept while the person signs in and consents, and afterwards so the same
/// request id from the same SP is never answered twice.
/// </summary>
public class SamlRequest
{
    /// <summary>A random handle carried through sign-in and consent.</summary>
    public string Handle { get; set; } = string.Empty;

    /// <summary>The service provider.</summary>
    public Guid ServiceProviderId { get; set; }

    /// <summary>The request's ID (InResponseTo); empty for an IdP-initiated sign-in.</summary>
    public string RequestId { get; set; } = string.Empty;

    /// <summary>Where the response goes.</summary>
    public string AcsUrl { get; set; } = string.Empty;

    /// <summary>The RelayState to return.</summary>
    public string? RelayState { get; set; }

    /// <summary>The assurance level the request asked for (RequestedAuthnContext), 0 for none.</summary>
    public int RequiredLevel { get; set; }

    /// <summary>Whether the request said ForceAuthn.</summary>
    public bool ForceAuthn { get; set; }

    /// <summary>When received.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>When the person must have finished.</summary>
    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>When answered.</summary>
    public DateTimeOffset? AnsweredAt { get; set; }
}
