using Sangam.Identity.Domain.Enums;

namespace Sangam.Identity.Domain;

/// <summary>
/// rc.16 (ASVS V1.4.4, V1.4.5; founder decision of 26 Oct 2026): what an operator may do on the operator console, one
/// named permission per feature. Code checks the permission, never the rank; ranks remain how permissions are given
/// (<see cref="OperatorRights"/>). Who may do what is unchanged from the ranks of docs/authority-model.md.
/// </summary>
public enum OperatorRight
{
    /// <summary>Use the console at all: its pages, the work queue, the grievance list.</summary>
    UseConsole,

    /// <summary>Search and list people.</summary>
    SearchUsers,

    /// <summary>Open a person's record (audited; the person sees it).</summary>
    OpenUserRecord,

    /// <summary>List applications.</summary>
    ListApplications,

    /// <summary>Read the grievance log.</summary>
    ReadGrievances,

    /// <summary>See the monitoring page.</summary>
    ViewMonitoring,

    /// <summary>Enable or disable a partner's application, set its logout addresses and DigiLocker exception, give it an owner.</summary>
    ManageApplications,

    /// <summary>Register and change SAML service providers.</summary>
    ManageSamlProviders,

    /// <summary>Change Sangam's own branding and messages (the platform scope).</summary>
    EditPlatformBranding,

    /// <summary>Suspend or reinstate an account.</summary>
    SuspendAccounts,

    /// <summary>Sign a person out everywhere.</summary>
    SignOutEverywhere,

    /// <summary>Place or clear a deletion hold.</summary>
    HoldDeletion,

    /// <summary>Read a person's audit trail.</summary>
    ReadAuditTrail,

    /// <summary>Review an account recovery waiting for a person's decision.</summary>
    ReviewRecoveries,

    /// <summary>Log, acknowledge, note and close grievances.</summary>
    ActOnGrievances,

    /// <summary>Withdraw a two-step reset before it takes effect.</summary>
    WithdrawTwoStepReset,

    /// <summary>See the console's overview of the whole platform.</summary>
    SeeOverview,

    /// <summary>Delete an account at once, without the grace period.</summary>
    DeleteAccounts,

    /// <summary>List, grant and revoke console access.</summary>
    ManageOperators,

    /// <summary>Apply a two-step reset now, without its waiting period.</summary>
    ApplyTwoStepResetNow,

    /// <summary>Review the recovery of a privileged account (an operator or an application or organisation administrator).</summary>
    ReviewPrivilegedRecoveries,
}

/// <summary>Which permissions each rank gives (rc.16). Ranks are cumulative: each has every permission of the ranks below it.</summary>
public static class OperatorRights
{
    private static readonly Dictionary<OperatorRight, PlatformRole> Lowest = new()
    {
        [OperatorRight.UseConsole] = PlatformRole.Viewer,
        [OperatorRight.SearchUsers] = PlatformRole.Viewer,
        [OperatorRight.OpenUserRecord] = PlatformRole.Viewer,
        [OperatorRight.ListApplications] = PlatformRole.Viewer,
        [OperatorRight.ReadGrievances] = PlatformRole.Viewer,
        [OperatorRight.ViewMonitoring] = PlatformRole.Viewer,
        [OperatorRight.ManageApplications] = PlatformRole.AppManager,
        [OperatorRight.ManageSamlProviders] = PlatformRole.AppManager,
        [OperatorRight.EditPlatformBranding] = PlatformRole.AppManager,
        [OperatorRight.SuspendAccounts] = PlatformRole.Support,
        [OperatorRight.SignOutEverywhere] = PlatformRole.Support,
        [OperatorRight.HoldDeletion] = PlatformRole.Support,
        [OperatorRight.ReadAuditTrail] = PlatformRole.Support,
        [OperatorRight.ReviewRecoveries] = PlatformRole.Support,
        [OperatorRight.ActOnGrievances] = PlatformRole.Support,
        [OperatorRight.WithdrawTwoStepReset] = PlatformRole.Support,
        [OperatorRight.SeeOverview] = PlatformRole.Owner,
        [OperatorRight.DeleteAccounts] = PlatformRole.Owner,
        [OperatorRight.ManageOperators] = PlatformRole.Owner,
        [OperatorRight.ApplyTwoStepResetNow] = PlatformRole.Owner,
        [OperatorRight.ReviewPrivilegedRecoveries] = PlatformRole.Owner,
    };

    /// <summary>Whether an operator of <paramref name="role"/> has <paramref name="permission"/>; never for someone who is not an operator.</summary>
    /// <param name="role">The operator's rank, or null for someone who is not an operator.</param>
    /// <param name="permission">The permission.</param>
    public static bool Allows(PlatformRole? role, OperatorRight permission)
        => role is PlatformRole rank && rank >= LowestRank(permission);

    /// <summary>The lowest rank that has <paramref name="permission"/>, for saying what is needed.</summary>
    /// <param name="permission">The permission.</param>
    public static PlatformRole LowestRank(OperatorRight permission)
        => Lowest.TryGetValue(permission, out PlatformRole rank)
            ? rank
            : throw new ArgumentOutOfRangeException(nameof(permission), permission, "Every permission is given by a rank.");

    /// <summary>Every permission <paramref name="role"/> gives, in declaration order.</summary>
    /// <param name="role">The rank.</param>
    public static IReadOnlyList<OperatorRight> For(PlatformRole role)
        => [.. Enum.GetValues<OperatorRight>().Where(p => Allows(role, p))];
}
