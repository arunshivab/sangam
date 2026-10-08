using Sangam.Identity.Domain.Enums;

namespace Sangam.Identity.Domain.Entities;

/// <summary>
/// A record an application asked a person to sign (PR-17, SGM-207 §5). The application registers it; the person
/// signs or declines in Sangam's ceremony, after a fresh two-factor authentication; the signed token binds person,
/// record hash, meaning and time (21 CFR 11.50 and 11.70 style).
/// </summary>
public sealed class SignatureRequest
{
    /// <summary>Primary key; also the signature token's <c>jti</c>.</summary>
    public Guid Id { get; set; }

    /// <summary>The application that asked.</summary>
    public Guid AppId { get; set; }

    /// <summary>The application's own id for the record.</summary>
    public string RecordId { get; set; } = string.Empty;

    /// <summary>The record's hash, <c>sha256:</c>, <c>sha384:</c> or <c>sha512:</c> and lowercase hex.</summary>
    public string RecordHash { get; set; } = string.Empty;

    /// <summary>What the signature means, for example "Approved" or "Reviewed".</summary>
    public string Meaning { get; set; } = string.Empty;

    /// <summary>What the person is shown about the record.</summary>
    public string DisplayText { get; set; } = string.Empty;

    /// <summary>The only person who may sign, when the application named one.</summary>
    public Guid? SignerUserId { get; set; }

    /// <summary>Where the person goes afterwards; one of the application's registered redirect URIs.</summary>
    public string ReturnUrl { get; set; } = string.Empty;

    /// <summary>Status.</summary>
    public SignatureStatus Status { get; set; }

    /// <summary>When the request was made (UTC).</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>After this, a pending request can no longer be signed (UTC).</summary>
    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>Who signed or declined.</summary>
    public Guid? DecidedByUserId { get; set; }

    /// <summary>When it was signed or declined (UTC).</summary>
    public DateTimeOffset? DecidedAt { get; set; }

    /// <summary>The assurance level of the authentication behind the signature.</summary>
    public string? Acr { get; set; }

    /// <summary>The RFC 8176 methods, space-separated.</summary>
    public string? Amr { get; set; }

    /// <summary>The signed signature token (a JWS), kept as evidence and for the application to fetch.</summary>
    public string? Token { get; set; }
}
