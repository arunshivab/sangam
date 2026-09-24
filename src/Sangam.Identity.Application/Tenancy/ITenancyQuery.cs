namespace Sangam.Identity.Application.Tenancy;

/// <summary>Read side of the tenancy model used when tokens are built.</summary>
public interface ITenancyQuery
{
    /// <summary>The user's live memberships in <paramref name="appId"/>, as claim elements.</summary>
    /// <param name="userId">The user.</param>
    /// <param name="appId">The app the token is for.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IReadOnlyList<OrgClaim>> GetOrgClaimsAsync(Guid userId, Guid appId, CancellationToken cancellationToken = default);
}
