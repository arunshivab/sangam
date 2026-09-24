namespace Sangam.Identity.Application.Apps;

/// <summary>Read access to the partner app registry.</summary>
public interface IAppDirectory
{
    /// <summary>Finds an app by its OAuth client id, or <see langword="null"/>.</summary>
    /// <param name="clientId">Client id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<AppSummary?> FindByClientIdAsync(string clientId, CancellationToken cancellationToken = default);

    /// <summary>Finds an app by id, or <see langword="null"/>.</summary>
    /// <param name="appId">App id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<AppSummary?> FindByIdAsync(Guid appId, CancellationToken cancellationToken = default);
}
