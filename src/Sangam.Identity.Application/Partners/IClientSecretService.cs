namespace Sangam.Identity.Application.Partners;

/// <summary>
/// rc.11 (R5; founder decision, 13 Oct 2026): an application's client secrets on the partner console. Every administrator
/// sees which secrets are live and when they end; only an owner makes or revokes one. At most two are live at once, and
/// the last live secret cannot be revoked, so an owner cannot lock their own application out.
/// <para>
/// rc.15 (founder decision of 22 Oct 2026, ASVS V2.10.1): and its public keys, for signing in with a key instead of a secret
/// (<c>private_key_jwt</c>). At most two keys are live at once. Once Sangam has seen the application sign in with a key,
/// the owner may revoke every secret; what is left must always be a secret or a key that has been used, so the last way
/// in cannot be removed.
/// </para>
/// </summary>
public interface IClientSecretService
{
    /// <summary>The application's secrets, or <see langword="null"/> for someone who does not administer it.</summary>
    /// <param name="userId">The administrator.</param>
    /// <param name="appId">The application.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<ClientSecretsView?> GetAsync(Guid userId, Guid appId, CancellationToken cancellationToken = default);

    /// <summary>Makes a new secret, valid for twelve months; it is returned once and never again.</summary>
    /// <param name="userId">The owner.</param>
    /// <param name="appId">The application.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<NewClientSecret> CreateAsync(Guid userId, Guid appId, CancellationToken cancellationToken = default);

    /// <summary>Revokes one of the owner's secrets, or (with <see langword="null"/>) the one Sangam issued.</summary>
    /// <param name="userId">The owner.</param>
    /// <param name="appId">The application.</param>
    /// <param name="secretId">The secret, or <see langword="null"/> for the one Sangam issued.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<PartnerResult> RevokeAsync(Guid userId, Guid appId, Guid? secretId, CancellationToken cancellationToken = default);

    /// <summary>rc.15: adds a public key the application's servers sign in with.</summary>
    /// <param name="userId">The owner.</param>
    /// <param name="appId">The application.</param>
    /// <param name="publicKey">A PEM public key or a JSON Web Key.</param>
    /// <param name="name">A name for it, so people can tell keys apart.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<PartnerResult> AddKeyAsync(Guid userId, Guid appId, string publicKey, string? name, CancellationToken cancellationToken = default);

    /// <summary>rc.15: removes one of the application's public keys.</summary>
    /// <param name="userId">The owner.</param>
    /// <param name="appId">The application.</param>
    /// <param name="keyId">The key (its row id).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<PartnerResult> RemoveKeyAsync(Guid userId, Guid appId, Guid keyId, CancellationToken cancellationToken = default);
}

/// <summary>An application's client secrets, newest first.</summary>
/// <param name="Confidential">Whether the application uses a secret at all.</param>
/// <param name="CanManage">Whether the viewer is an owner, who may make and revoke secrets.</param>
/// <param name="Issued">The secret Sangam issued from its configuration, when it is live.</param>
/// <param name="Owned">The live secrets the application's owners made.</param>
/// <param name="Keys">rc.15: the live public keys it signs in with.</param>
public sealed record ClientSecretsView(bool Confidential, bool CanManage, IssuedSecretRow? Issued, IReadOnlyList<ClientSecretRow> Owned, IReadOnlyList<ClientKeyRow> Keys)
{
    /// <summary>How many secrets work now.</summary>
    public int LiveCount => (Issued is null ? 0 : 1) + Owned.Count;

    /// <summary>rc.15: how many ways in work now, secrets and keys together; the last one cannot be removed.</summary>
    public int CredentialCount => LiveCount + Keys.Count;
}

/// <summary>rc.15: a public key the application signs in with.</summary>
/// <param name="Id">Its row id.</param>
/// <param name="KeyId">Its key id (<c>kid</c>), which the application puts in its assertions' header.</param>
/// <param name="Name">Its name.</param>
/// <param name="Kind">What kind of key it is: <c>RSA 3072</c>, <c>EC P-256</c>.</param>
/// <param name="CreatedAt">When it was added.</param>
/// <param name="CreatedBy">Who added it.</param>
/// <param name="LastUsedAt">When the application last signed in with it (to the hour).</param>
public sealed record ClientKeyRow(Guid Id, string KeyId, string Name, string Kind, DateTimeOffset CreatedAt, string CreatedBy, DateTimeOffset? LastUsedAt);

/// <summary>The secret Sangam issued.</summary>
/// <param name="SetAt">When it was set, when known (rc.10 onwards).</param>
public sealed record IssuedSecretRow(DateTimeOffset? SetAt);

/// <summary>A secret an owner made.</summary>
/// <param name="Id">Its id.</param>
/// <param name="Hint">Its first and last characters.</param>
/// <param name="CreatedAt">When it was made.</param>
/// <param name="CreatedBy">Who made it.</param>
/// <param name="ExpiresAt">When it stops working.</param>
/// <param name="LastUsedAt">When the application last used it (to the hour).</param>
public sealed record ClientSecretRow(Guid Id, string Hint, DateTimeOffset CreatedAt, string CreatedBy, DateTimeOffset ExpiresAt, DateTimeOffset? LastUsedAt);

/// <summary>The outcome of making a secret.</summary>
/// <param name="Succeeded">Whether it was made.</param>
/// <param name="Message">What to tell the owner.</param>
/// <param name="Secret">The secret itself, shown once.</param>
public sealed record NewClientSecret(bool Succeeded, string Message, string? Secret);
