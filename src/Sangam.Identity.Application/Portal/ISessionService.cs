using Sangam.Identity.Domain.Enums;

namespace Sangam.Identity.Application.Portal;

/// <summary>Records and validates browser sessions. Used by the identity server's cookie pipeline.</summary>
public interface ISessionService
{
    /// <summary>Records a new session and returns its id, which the caller puts in the session cookie.</summary>
    /// <param name="userId">The signed-in user.</param>
    /// <param name="mode">How they signed in.</param>
    /// <param name="appId">The app that started the flow, if any.</param>
    /// <param name="deviceLabel">Label the app supplied, if any.</param>
    /// <param name="ipAddress">Client IP.</param>
    /// <param name="userAgent">Client user agent.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<Guid> StartAsync(Guid userId, SignInMode mode, Guid? appId, string? deviceLabel, string? ipAddress, string? userAgent, CancellationToken cancellationToken = default);

    /// <summary>
    /// Confirms the session is still live and refreshes its last-seen time. Returns
    /// <see langword="false"/> when the row is missing or revoked, which ends the cookie.
    /// </summary>
    /// <param name="sessionId">Session id from the cookie.</param>
    /// <param name="userId">User id from the cookie.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<bool> TouchAsync(Guid sessionId, Guid userId, CancellationToken cancellationToken = default);

    /// <summary>Ends one session (used by sign-out).</summary>
    /// <param name="sessionId">Session id.</param>
    /// <param name="reason">Why it ended.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task EndAsync(Guid sessionId, string reason, CancellationToken cancellationToken = default);
}
