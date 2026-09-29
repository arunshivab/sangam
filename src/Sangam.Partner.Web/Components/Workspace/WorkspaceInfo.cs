using Sangam.Identity.Domain.Enums;

namespace Sangam.Partner.Web.Components.Workspace;

/// <summary>The application being worked on, and who is working on it.</summary>
/// <param name="UserId">The signed-in administrator.</param>
/// <param name="AppId">The application.</param>
/// <param name="AppName">Its name.</param>
/// <param name="Role">Their rank over it.</param>
public sealed record WorkspaceInfo(Guid UserId, Guid AppId, string AppName, AppAdminRole Role)
{
    /// <summary>Whether they may manage the application's administrators.</summary>
    public bool IsOwner => Role == AppAdminRole.Owner;
}
