using Sangam.Identity.Domain;
using Sangam.Identity.Domain.Enums;

namespace Sangam.Admin.Web.Components.Common;

/// <summary>The signed-in operator, cascaded to every console page once the gate is cleared.</summary>
/// <param name="UserId">Their Sangam user id.</param>
/// <param name="Role">Their rank on the console.</param>
/// <param name="Name">Their display name.</param>
public sealed record ConsoleContext(Guid UserId, PlatformRole Role, string Name)
{
    /// <summary>Whether they may act on a user account.</summary>
    public bool CanAct => Role >= PlatformRole.Support;

    /// <summary>Whether they may register and configure applications.</summary>
    public bool CanManageApps => Role >= PlatformRole.AppManager;

    /// <summary>Whether they may manage operators and delete accounts outright.</summary>
    public bool IsOwner => Role == PlatformRole.Owner;

    /// <summary>The rank as a word for the top bar.</summary>
    public string RoleLabel => PlatformRanks.Label(Role);
}
