namespace Sangam.Identity.Application.Apps;

/// <summary>
/// rc.14 (decision record 0017): pairwise subject ids. Every application knows a person by its own id, never by their
/// Sangam id and never by another application's id. Everything an application is given (tokens, userinfo, the management
/// API, webhooks, provisioning, logout tokens, signature tokens) carries its id; everything it sends back is translated
/// with <see cref="UserAsync"/>, and an id it does not know is answered as not found. Sangam's own applications (the portal
/// and the consoles) know people by their Sangam id.
/// </summary>
public interface ISubjectIds
{
    /// <summary>The id <paramref name="appId"/> knows <paramref name="userId"/> by, given out now if it has none yet.</summary>
    /// <param name="appId">The application.</param>
    /// <param name="userId">The person's Sangam id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<Guid> SubjectAsync(Guid appId, Guid userId, CancellationToken cancellationToken = default);

    /// <summary>The ids <paramref name="appId"/> knows several people by, given out now where it has none yet.</summary>
    /// <param name="appId">The application.</param>
    /// <param name="userIds">The people's Sangam ids.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Sangam id → the application's id, for every person asked about.</returns>
    Task<IReadOnlyDictionary<Guid, Guid>> SubjectsAsync(Guid appId, IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken = default);

    /// <summary>The person an application's id names, or <see langword="null"/> when the application has no such id.</summary>
    /// <param name="appId">The application.</param>
    /// <param name="subject">The id the application sent.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<Guid?> UserAsync(Guid appId, Guid subject, CancellationToken cancellationToken = default);

    /// <summary>
    /// rc.14: the person's sign-in session an application's <c>sid</c> names (<see cref="AppSessionIds"/>);
    /// <see langword="null"/> when none.
    /// </summary>
    /// <param name="appId">The application.</param>
    /// <param name="userId">The person.</param>
    /// <param name="appSessionId">The <c>sid</c> the application has.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<Guid?> SessionAsync(Guid appId, Guid userId, Guid appSessionId, CancellationToken cancellationToken = default);
}
