using Sangam.Identity.Application.Accounts;
using Sangam.Identity.Application.Portal;
using Sangam.Identity.Domain.Enums;

namespace Sangam.SelfService.Web.Components.Common;

/// <summary>rc.10 (P1): what would make the account safer, each with where to do it.</summary>
public enum CheckupItem
{
    /// <summary>No authenticator app and no passkey.</summary>
    AddStrongMethod = 0,

    /// <summary>A password alone signs in (no authenticator, and no emailed code after it).</summary>
    TwoStepEveryTime = 1,

    /// <summary>The mobile number is not verified, so it cannot help the person get back in.</summary>
    VerifyMobile = 2,

    /// <summary>Applications not used for three months still have access.</summary>
    DormantApps = 3,

    /// <summary>Sangam asked the person to verify with DigiLocker once more (rc.9).</summary>
    Reverify = 4,
}

/// <summary>rc.10 (P1): the security checkup on the portal's overview.</summary>
public static class SecurityCheckup
{
    /// <summary>What is left to do, the most protective first.</summary>
    /// <param name="user">The person.</param>
    /// <param name="authenticator">Whether an authenticator app is set up.</param>
    /// <param name="passkeys">How many passkeys they have.</param>
    /// <param name="apps">The applications with access.</param>
    /// <param name="now">The time now.</param>
    /// <returns>The items.</returns>
    public static IReadOnlyList<CheckupItem> For(UserSummary user, bool authenticator, int passkeys, IReadOnlyList<LinkedApp> apps, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(apps);
        List<CheckupItem> items = [];
        if (!authenticator && passkeys == 0)
        {
            items.Add(CheckupItem.AddStrongMethod);
        }

        if (!authenticator && user.SignInPreference == SignInMode.Password)
        {
            items.Add(CheckupItem.TwoStepEveryTime);
        }

        if (!user.MobileVerified)
        {
            items.Add(CheckupItem.VerifyMobile);
        }

        if (apps.Any(a => a.StatusAt(now) == LinkedAppStatus.Dormant))
        {
            items.Add(CheckupItem.DormantApps);
        }

        if (user.IdentityReverifyRequestedAt is not null)
        {
            items.Add(CheckupItem.Reverify);
        }

        return items;
    }
}
