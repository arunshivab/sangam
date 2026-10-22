namespace Sangam.Identity.Domain.Entities;

/// <summary>
/// rc.15 (founder decision of 22 Oct 2026, ASVS V2.10.1): a public key an application's owner added on the partner
/// console, so its servers authenticate to Sangam by signing a short-lived assertion with the private key
/// (<c>private_key_jwt</c>, RFC 7523) instead of sending a client secret. Sangam keeps only the public key; the private key
/// never leaves the application's servers. Two keys can be live at once, so an owner moves to a new key with no break.
/// A key does not expire on its own; the owner removes it.
/// </summary>
public sealed class ClientKey
{
    /// <summary>The most keys live at once.</summary>
    public const int MaxLive = 2;

    /// <summary>Primary key.</summary>
    public Guid Id { get; set; }

    /// <summary>The application.</summary>
    public Guid AppId { get; set; }

    /// <summary>The key's id (<c>kid</c>): its RFC 7638 thumbprint, which the application puts in its assertions' header.</summary>
    public string KeyId { get; set; } = string.Empty;

    /// <summary>The public key as a JSON Web Key (no private parts).</summary>
    public string Jwk { get; set; } = string.Empty;

    /// <summary>What kind of key it is, for people: <c>RSA 3072</c>, <c>EC P-256</c>.</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>A name the owner gave it, so people can tell keys apart.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>When it was added.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Who added it.</summary>
    public Guid CreatedByUserId { get; set; }

    /// <summary>When it was removed, if it was.</summary>
    public DateTimeOffset? RevokedAt { get; set; }

    /// <summary>Who removed it.</summary>
    public Guid? RevokedByUserId { get; set; }

    /// <summary>When an application last signed in with it (to the hour).</summary>
    public DateTimeOffset? LastUsedAt { get; set; }
}
