namespace Sangam.Identity.Domain.Entities;

/// <summary>A passkey (WebAuthn public-key credential) registered to an account (PR-14, SGM-205 §3).</summary>
public class PasskeyCredential
{
    /// <summary>Identifier.</summary>
    public Guid Id { get; set; }

    /// <summary>The owner.</summary>
    public Guid UserId { get; set; }

    /// <summary>The credential id chosen by the authenticator.</summary>
    public byte[] CredentialId { get; set; } = [];

    /// <summary>The COSE-encoded public key.</summary>
    public byte[] PublicKey { get; set; } = [];

    /// <summary>The last signature counter seen; one that goes backwards suggests a cloned authenticator.</summary>
    public long SignCount { get; set; }

    /// <summary>The authenticator model, when it reports one.</summary>
    public Guid AaGuid { get; set; }

    /// <summary>Transports the authenticator reported (usb, nfc, ble, internal, hybrid).</summary>
    public string Transports { get; set; } = string.Empty;

    /// <summary>Whether the passkey may be synced between devices.</summary>
    public bool IsBackupEligible { get; set; }

    /// <summary>Whether the passkey was synced at its last use.</summary>
    public bool IsBackedUp { get; set; }

    /// <summary>The person's own name for it, for example "My phone".</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>When it was added.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>When it was last used to sign in.</summary>
    public DateTimeOffset? LastUsedAt { get; set; }

    /// <summary>When it was removed. Rows are kept, never deleted.</summary>
    public DateTimeOffset? RevokedAt { get; set; }
}
