namespace Sangam.Identity.Domain.Entities;

/// <summary>
/// Sangam's record of a passkey on an account (PR-14, SGM-205 §3; D-G). The key itself — public key, signature
/// counter, transports, flags — is held by ASP.NET Core Identity's own passkey store (<c>user_passkeys</c>), which
/// verifies every ceremony. This row keeps what Identity does not: the id the screens use, the person's name for
/// the passkey, when it was last used, and its removal, which is kept rather than deleted.
/// </summary>
public class PasskeyCredential
{
    /// <summary>Identifier.</summary>
    public Guid Id { get; set; }

    /// <summary>The owner.</summary>
    public Guid UserId { get; set; }

    /// <summary>The credential id chosen by the authenticator (the key of Identity's passkey record).</summary>
    public byte[] CredentialId { get; set; } = [];

    /// <summary>Whether the passkey may be synced between devices.</summary>
    public bool IsBackupEligible { get; set; }

    /// <summary>The person's own name for it, for example "My phone".</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>When it was added.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>When it was last used to sign in.</summary>
    public DateTimeOffset? LastUsedAt { get; set; }

    /// <summary>When it was removed. Rows are kept, never deleted; Identity's key record is deleted.</summary>
    public DateTimeOffset? RevokedAt { get; set; }
}
