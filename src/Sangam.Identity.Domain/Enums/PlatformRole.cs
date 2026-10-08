namespace Sangam.Identity.Domain.Enums;

/// <summary>
/// The rank a platform operator (the Sangam team) holds on the admin console.
/// <para>
/// Everyone in <c>platform_operators</c> is an operator; this is how much they may do. The ranks
/// are ordered and cumulative, so every check is a comparison: <c>role &gt;= PlatformRole.Support</c>.
/// A rank that had to be forbidden something a lower rank may do would break that model and would
/// need capability flags instead.
/// </para>
/// </summary>
public enum PlatformRole
{
    /// <summary>May look and nothing else: search users, open a record, list applications.</summary>
    Viewer = 0,

    /// <summary>Everything a viewer may do, plus registering and configuring applications. Never acts on a user account.</summary>
    AppManager = 1,

    /// <summary>Everything an app manager may do, plus acting on a user: suspend, reinstate, sign out everywhere, hold a deletion.</summary>
    Support = 2,

    /// <summary>Everything support may do, plus granting and revoking console access and deleting an account outright.</summary>
    Owner = 3,
}
