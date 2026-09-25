namespace Sangam.Identity.Application.Portal;

/// <summary>Everything the self-service portal reads and changes on behalf of the signed-in user.</summary>
public interface IPortalService
{
    /// <summary>Counters for the dashboard.</summary>
    Task<PortalOverview> GetOverviewAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>Applications the user has granted access to, most recently used first.</summary>
    Task<IReadOnlyList<LinkedApp>> GetLinkedAppsAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>Revokes an application: its consent, its app grant and every token it holds.</summary>
    Task<bool> RevokeAppAsync(Guid userId, Guid appId, string? ipAddress, CancellationToken cancellationToken = default);

    /// <summary>Live sessions, the current one first.</summary>
    Task<IReadOnlyList<PortalSession>> GetSessionsAsync(Guid userId, Guid? currentSessionId, CancellationToken cancellationToken = default);

    /// <summary>Ends one session. Returns <see langword="false"/> when it is not the user's or already ended.</summary>
    Task<bool> RevokeSessionAsync(Guid userId, Guid sessionId, string? ipAddress, CancellationToken cancellationToken = default);

    /// <summary>Ends every session and rotates the security stamp, so all devices are signed out.</summary>
    Task RevokeAllSessionsAsync(Guid userId, string? ipAddress, CancellationToken cancellationToken = default);

    /// <summary>A page of the user's audit trail within <paramref name="since"/>.</summary>
    Task<AuditPage> GetAuditAsync(Guid userId, DateTimeOffset? since, int page, int pageSize, CancellationToken cancellationToken = default);

    /// <summary>Everything Sangam holds about the user, as indented JSON (DPDPA portability).</summary>
    Task<string> ExportAsync(Guid userId, string? ipAddress, CancellationToken cancellationToken = default);

    /// <summary>Current deletion state.</summary>
    Task<DeletionState> GetDeletionStateAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>Starts the 30-day grace period, blocks sign-in, ends sessions and revokes every app.</summary>
    Task RequestDeletionAsync(Guid userId, string? ipAddress, CancellationToken cancellationToken = default);

    /// <summary>Cancels a pending deletion and reactivates the account.</summary>
    Task CancelDeletionAsync(Guid userId, string? ipAddress, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates the parts of the profile the user may change today: name, language, gender and
    /// mobile. Email is fixed (changing a sign-in identifier needs its own verified flow), and so
    /// is date of birth until identity verification exists. A changed mobile is marked unverified.
    /// </summary>
    /// <param name="userId">The user.</param>
    /// <param name="update">The new values.</param>
    /// <param name="ipAddress">Client IP, for the audit entry.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<ProfileUpdateResult> UpdateProfileAsync(Guid userId, ProfileUpdate update, string? ipAddress, CancellationToken cancellationToken = default);
}
