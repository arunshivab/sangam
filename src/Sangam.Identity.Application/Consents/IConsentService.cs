namespace Sangam.Identity.Application.Consents;

/// <summary>
/// Records and checks a user's consent for an app. A consent is valid for the app's current
/// consent version and covers exactly the scopes that were granted; asking for a scope
/// outside that set, or a version change, sends the user back to the consent screen.
/// </summary>
public interface IConsentService
{
    /// <summary>Whether a live consent covers every requested scope at the app's current version.</summary>
    /// <param name="userId">The user.</param>
    /// <param name="appId">The app.</param>
    /// <param name="requestedScopes">Scopes the authorization request asks for.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<bool> HasValidConsentAsync(Guid userId, Guid appId, IReadOnlyCollection<string> requestedScopes, CancellationToken cancellationToken = default);

    /// <summary>Records a grant (a new row; any previous live consent for the app is revoked) and ensures the app grant exists.</summary>
    /// <param name="userId">The user.</param>
    /// <param name="appId">The app.</param>
    /// <param name="scopes">Scopes granted.</param>
    /// <param name="ipAddress">Client IP.</param>
    /// <param name="userAgent">Client user agent.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task GrantAsync(Guid userId, Guid appId, IReadOnlyCollection<string> scopes, string? ipAddress, string? userAgent, CancellationToken cancellationToken = default);

    /// <summary>Audits a denial; nothing is stored beyond the audit row.</summary>
    /// <param name="userId">The user.</param>
    /// <param name="appId">The app.</param>
    /// <param name="ipAddress">Client IP.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task DenyAsync(Guid userId, Guid appId, string? ipAddress, CancellationToken cancellationToken = default);
}
