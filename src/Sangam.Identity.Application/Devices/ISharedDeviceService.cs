using Sangam.Identity.Domain.Enums;

namespace Sangam.Identity.Application.Devices;

/// <summary>rc.12 (T1): an enrolled shared ward device, as the sign-in pages see it.</summary>
/// <param name="Id">The device.</param>
/// <param name="AppId">The application whose organisation tree it belongs to.</param>
/// <param name="OrgId">The organisation it is at.</param>
/// <param name="Label">Its label ("Ward 4B · Nursing station").</param>
/// <param name="OrgName">The organisation's name.</param>
/// <param name="Rules">The rules for getting back in, after the customer's stricter choices.</param>
public sealed record SharedDeviceInfo(Guid Id, Guid AppId, Guid OrgId, string Label, string OrgName, DeviceRules Rules);

/// <summary>rc.13: one organisation's device rules, as its admin sees them.</summary>
/// <param name="Rules">What holds there.</param>
/// <param name="Limit">The most it may be: what holds just above it (Sangam's own at a customer).</param>
/// <param name="SetHere">Whether this organisation set either rule itself (otherwise it follows the ones above).</param>
public sealed record DeviceRulesHere(DeviceRules Rules, DeviceRules Limit, bool SetHere);

/// <summary>rc.12 (T1): how a person gets back in on a shared device.</summary>
/// <param name="PinMinutes">How long after a lock a PIN works (at most 120).</param>
/// <param name="FullSignInHours">How often a full sign-in is needed (at most 12).</param>
public sealed record DeviceRules(int PinMinutes, int FullSignInHours)
{
    /// <summary>Sangam's rules, which an organisation may only make stricter.</summary>
    public static DeviceRules Sangam { get; } = new(120, 12);
}

/// <summary>rc.12 (T1): one person's tile on "Who's using this tablet?".</summary>
/// <param name="Id">The tile's id (the person on this device, not their account id).</param>
/// <param name="Name">Their name.</param>
/// <param name="Role">Their role at the device's organisation, if any.</param>
/// <param name="Locked">Whether their session here is locked and a PIN may unlock it now.</param>
public sealed record DeviceTile(Guid Id, string Name, string? Role, bool Locked);

/// <summary>rc.12 (T1): a full sign-in that just happened on an enrolled device.</summary>
/// <param name="UserId">The person.</param>
/// <param name="SessionId">The new session.</param>
/// <param name="Mode">How they signed in.</param>
/// <param name="Methods">The RFC 8176 methods.</param>
/// <param name="SecondFactor">Whether a second factor was used.</param>
public sealed record DeviceSignIn(Guid UserId, Guid SessionId, SignInMode Mode, IReadOnlyList<string> Methods, bool SecondFactor);

/// <summary>rc.12 (T1): a locked session given back after a right PIN, to issue its cookie again.</summary>
/// <param name="UserId">The person.</param>
/// <param name="SessionId">The same session as before the lock.</param>
/// <param name="Mode">How they last signed in fully.</param>
/// <param name="Methods">The RFC 8176 methods of that sign-in.</param>
/// <param name="SecondFactor">Whether it had a second factor.</param>
/// <param name="AuthenticatedAt">When it happened (the session still ends twelve hours after it).</param>
public sealed record DeviceResume(Guid UserId, Guid SessionId, SignInMode Mode, IReadOnlyList<string> Methods, bool SecondFactor, DateTimeOffset AuthenticatedAt);

/// <summary>rc.12 (T1): outcome of a PIN.</summary>
public enum PinUnlockStatus
{
    /// <summary>Right PIN: the session is given back.</summary>
    Unlocked = 0,

    /// <summary>Wrong PIN; tries remain.</summary>
    Wrong = 1,

    /// <summary>The fifth wrong PIN, or the PIN was already cancelled: a full sign-in is needed.</summary>
    Cancelled = 2,

    /// <summary>Too long since the lock or the last full sign-in, or the session ended: a full sign-in is needed.</summary>
    SignInAgain = 3,

    /// <summary>The person has no PIN.</summary>
    NoPin = 4,
}

/// <summary>rc.12 (T1): outcome of a PIN.</summary>
/// <param name="Status">What happened.</param>
/// <param name="Resume">The session given back, when unlocked.</param>
/// <param name="TriesLeft">Wrong PINs left before it is cancelled.</param>
public sealed record PinUnlockResult(PinUnlockStatus Status, DeviceResume? Resume = null, int TriesLeft = 0);

/// <summary>rc.12 (T1): what the authorization endpoint must do before going on, on this browser.</summary>
public enum DeviceGate
{
    /// <summary>Nothing: go on.</summary>
    None = 0,

    /// <summary>The session is locked and this device may unlock it with a PIN.</summary>
    Unlock = 1,

    /// <summary>The session is locked and cannot be unlocked here: sign in fully.</summary>
    SignInAgain = 2,

    /// <summary>Offer to set a PIN (once per sign-in on an enrolled device).</summary>
    OfferPin = 3,
}

/// <summary>rc.12 (T1): outcome of starting enrolment.</summary>
public enum DeviceEnrolStatus
{
    /// <summary>Started: open the link on the device.</summary>
    Started = 0,

    /// <summary>The person does not administer that organisation.</summary>
    NotAdmin = 1,

    /// <summary>A label is needed.</summary>
    LabelRequired = 2,
}

/// <summary>rc.12 (T1): enrolment started.</summary>
/// <param name="Status">What happened.</param>
/// <param name="Token">The one-time token for <c>/device/enrol</c>, when started.</param>
public sealed record DeviceEnrolStart(DeviceEnrolStatus Status, string? Token = null);

/// <summary>rc.12 (T1): the device took its key.</summary>
/// <param name="DeviceId">The device.</param>
/// <param name="Label">Its label.</param>
/// <param name="Key">The device key, to keep in this browser only.</param>
public sealed record DeviceEnrolled(Guid DeviceId, string Label, string Key);

/// <summary>rc.12 (T1): a shared device in the organisation console.</summary>
/// <param name="Id">The device.</param>
/// <param name="Label">Its label.</param>
/// <param name="OrgName">Where it is.</param>
/// <param name="EnrolledBy">Who enrolled it.</param>
/// <param name="EnrolledAt">When it took its key; <see langword="null"/> while enrolment waits.</param>
/// <param name="LastSeenAt">When someone last signed in or unlocked on it.</param>
/// <param name="People">How many people have used it.</param>
public sealed record SharedDeviceRow(Guid Id, string Label, string OrgName, string EnrolledBy, DateTimeOffset? EnrolledAt, DateTimeOffset? LastSeenAt, int People);

/// <summary>
/// rc.12 (T1; design section 8): shared ward devices. Admins enrol a device from the device itself; the sign-in page there
/// shows the people who used it; applications lock a session when their idle timer runs out; Sangam decides how the person
/// gets back in — a PIN within two hours of the lock, a full sign-in at least every twelve hours, five wrong PINs cancel the
/// PIN — and a customer may make those stricter, never looser.
/// </summary>
public interface ISharedDeviceService
{
    /// <summary>Starts enrolling a device: a one-time link valid for 15 minutes, to open on the device itself.</summary>
    /// <param name="appId">The application.</param>
    /// <param name="adminUserId">The admin.</param>
    /// <param name="orgId">Where the device is.</param>
    /// <param name="label">Its label.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The outcome.</returns>
    Task<DeviceEnrolStart> StartEnrolAsync(Guid appId, Guid adminUserId, Guid orgId, string label, CancellationToken cancellationToken = default);

    /// <summary>Completes enrolment on the device, signed in as the admin who started it.</summary>
    /// <param name="token">The one-time token.</param>
    /// <param name="userId">The signed-in person.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The device and its key, or <see langword="null"/> when the token is wrong, used, expired or not theirs.</returns>
    Task<DeviceEnrolled?> CompleteEnrolAsync(string token, Guid userId, CancellationToken cancellationToken = default);

    /// <summary>The enrolled device this browser's key belongs to.</summary>
    /// <param name="key">The key from the browser's cookie.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The device, or <see langword="null"/>.</returns>
    Task<SharedDeviceInfo?> FindAsync(string? key, CancellationToken cancellationToken = default);

    /// <summary>The devices in an admin's organisation and everything under it.</summary>
    /// <param name="appId">The application.</param>
    /// <param name="adminUserId">The admin.</param>
    /// <param name="orgId">The organisation.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The devices, most recently used first.</returns>
    Task<IReadOnlyList<SharedDeviceRow>> ListAsync(Guid appId, Guid adminUserId, Guid orgId, CancellationToken cancellationToken = default);

    /// <summary>Removes a device at once; its locked sessions end.</summary>
    /// <param name="appId">The application.</param>
    /// <param name="adminUserId">The admin.</param>
    /// <param name="deviceId">The device.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><see langword="true"/> when removed.</returns>
    Task<bool> RemoveAsync(Guid appId, Guid adminUserId, Guid deviceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The rules that hold at an organisation: the shortest set there or above it (rc.13; rc.12 had them per customer only),
    /// never longer than Sangam's.
    /// </summary>
    /// <param name="orgId">The organisation (a customer, branch, department or ward).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The rules.</returns>
    Task<DeviceRules> RulesAsync(Guid orgId, CancellationToken cancellationToken = default);

    /// <summary>rc.13: an organisation's rules as its admin sees them: what holds, the limit from above, and whether set here.</summary>
    /// <param name="appId">The application.</param>
    /// <param name="adminUserId">An admin of the organisation (or above it, or of the application).</param>
    /// <param name="orgId">The organisation.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The rules, or <see langword="null"/> when the person is not an admin there.</returns>
    Task<DeviceRulesHere?> RulesHereAsync(Guid appId, Guid adminUserId, Guid orgId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets an organisation's rules (rc.13: any organisation, not only a customer). Only stricter than the organisations above
    /// it are kept; the same as above means it follows them.
    /// </summary>
    /// <param name="appId">The application.</param>
    /// <param name="adminUserId">An admin of the organisation (or above it, or of the application).</param>
    /// <param name="orgId">The organisation.</param>
    /// <param name="rules">The rules.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><see langword="false"/> when the person is not an admin there.</returns>
    Task<bool> SetRulesAsync(Guid appId, Guid adminUserId, Guid orgId, DeviceRules rules, CancellationToken cancellationToken = default);

    /// <summary>The last six people on the device who still work at its hospital, most recent first.</summary>
    /// <param name="deviceId">The device.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The tiles.</returns>
    Task<IReadOnlyList<DeviceTile>> TilesAsync(Guid deviceId, CancellationToken cancellationToken = default);

    /// <summary>The account behind a tile on this device, for picking it on the sign-in page.</summary>
    /// <param name="deviceId">The device.</param>
    /// <param name="tileId">The tile.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The account id and name, or <see langword="null"/>.</returns>
    Task<(Guid UserId, string Name)?> TileAsync(Guid deviceId, Guid tileId, CancellationToken cancellationToken = default);

    /// <summary>Records a full sign-in on the device: the person's tile, their session's device, and a fresh start for their PIN.</summary>
    /// <param name="deviceId">The device.</param>
    /// <param name="signIn">The sign-in.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task SignedInAsync(Guid deviceId, DeviceSignIn signIn, CancellationToken cancellationToken = default);

    /// <summary>What must happen before an authorization request goes on.</summary>
    /// <param name="sessionId">The session from the cookie.</param>
    /// <param name="deviceId">The enrolled device of this browser, if any.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The gate.</returns>
    Task<DeviceGate> GateAsync(Guid sessionId, Guid? deviceId, CancellationToken cancellationToken = default);

    /// <summary>Records that the person was offered a PIN in this session, whether or not they set one.</summary>
    /// <param name="deviceId">The device.</param>
    /// <param name="userId">The person.</param>
    /// <param name="sessionId">The session.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task PinOfferedAsync(Guid deviceId, Guid userId, Guid sessionId, CancellationToken cancellationToken = default);

    /// <summary>Sets the person's 6-digit PIN.</summary>
    /// <param name="userId">The person.</param>
    /// <param name="pin">The PIN.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><see langword="false"/> when the PIN is not six digits or too easy to guess.</returns>
    Task<bool> SetPinAsync(Guid userId, string pin, CancellationToken cancellationToken = default);

    /// <summary>Whether the person has a PIN that works.</summary>
    /// <param name="userId">The person.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><see langword="true"/> when they do.</returns>
    Task<bool> HasPinAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// An application locks the person's session (its idle timer ran out). The person's other applications in the session are
    /// told (<c>session.locked</c>).
    /// </summary>
    /// <param name="sessionId">The session (the token's <c>sid</c>).</param>
    /// <param name="userId">The person (the token's subject).</param>
    /// <param name="appId">The application locking it.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><see langword="false"/> when there is no such live session.</returns>
    Task<bool> LockAsync(Guid sessionId, Guid userId, Guid appId, CancellationToken cancellationToken = default);

    /// <summary>The tile of the person whose session this is, on this device, when it is locked.</summary>
    /// <param name="deviceId">The device.</param>
    /// <param name="sessionId">The session.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The tile id, or <see langword="null"/>.</returns>
    Task<Guid?> LockedTileAsync(Guid deviceId, Guid sessionId, CancellationToken cancellationToken = default);

    /// <summary>Unlocks with a PIN on the device.</summary>
    /// <param name="deviceId">The device.</param>
    /// <param name="tileId">The person's tile.</param>
    /// <param name="pin">The PIN typed.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The outcome.</returns>
    Task<PinUnlockResult> UnlockAsync(Guid deviceId, Guid tileId, string pin, CancellationToken cancellationToken = default);
}
