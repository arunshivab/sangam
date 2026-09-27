using Sangam.Identity.Domain.Enums;

namespace Sangam.Identity.Domain;

/// <summary>Words for the platform ranks, so the console and the audit log say the same thing.</summary>
public static class PlatformRanks
{
    /// <summary>The rank as a lowercase label: <c>viewer</c>, <c>app manager</c>, <c>support</c>, <c>owner</c>.</summary>
    /// <param name="role">The rank.</param>
    public static string Label(PlatformRole role) => role switch
    {
        PlatformRole.Owner => "owner",
        PlatformRole.Support => "support",
        PlatformRole.AppManager => "app manager",
        _ => "viewer",
    };

    /// <summary>One line describing what the rank may do, shown beside it when granting access.</summary>
    /// <param name="role">The rank.</param>
    public static string Describe(PlatformRole role) => role switch
    {
        PlatformRole.Owner => "Everything, including granting console access and deleting an account outright.",
        PlatformRole.Support => "Act on user accounts: suspend, reinstate, sign out everywhere, hold a deletion.",
        PlatformRole.AppManager => "Register and configure applications. Never acts on a user account.",
        _ => "Look only: search users, open a record, list applications.",
    };

    /// <summary>Every rank, lowest first, for a dropdown.</summary>
    public static IReadOnlyList<PlatformRole> All { get; } =
        [PlatformRole.Viewer, PlatformRole.AppManager, PlatformRole.Support, PlatformRole.Owner];
}
