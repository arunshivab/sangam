using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Sangam.Identity.Application.Accounts;
using Sangam.Identity.Application.Devices;
using Sangam.Identity.Server.Authentication;

namespace Sangam.Identity.Server.Pages.Account;

/// <summary>
/// rc.12 (T1): an application locked the person's screen on an enrolled shared device. Within the PIN window they tap their
/// tile and type their PIN, and the same session comes back; anyone else signs in as themselves, and the locked session
/// waits, still locked, for its owner.
/// </summary>
public sealed class UnlockModel : AuthPageModel
{
    private readonly ISharedDeviceService _devices;
    private readonly IAccountService _accounts;

    /// <summary>Initialises the page.</summary>
    /// <param name="devices">Shared devices.</param>
    /// <param name="accounts">Accounts.</param>
    public UnlockModel(ISharedDeviceService devices, IAccountService accounts)
    {
        _devices = devices ?? throw new ArgumentNullException(nameof(devices));
        _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
    }

    /// <summary>The authorization request to go back to.</summary>
    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    /// <summary>The tile chosen on the sign-in page, when not the session in this browser.</summary>
    [BindProperty(SupportsGet = true)]
    public Guid? Tile { get; set; }

    /// <summary>The PIN typed.</summary>
    [BindProperty]
    public string Pin { get; set; } = string.Empty;

    /// <summary>The device.</summary>
    public SharedDeviceInfo Device { get; private set; } = null!;

    /// <summary>The person's tile.</summary>
    public DeviceTile? Person { get; private set; }

    /// <summary>What went wrong.</summary>
    public string? Error { get; private set; }

    /// <summary>Whether only a full sign-in is left.</summary>
    public bool SignInOnly { get; private set; }

    /// <summary>Shows the tile and the PIN.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The page, or the sign-in page when there is nothing to unlock here.</returns>
    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
        => await LoadAsync(cancellationToken) ? Page() : await SignInFullyAsync();

    /// <summary>Checks the PIN.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Back to the application, or the page with what went wrong.</returns>
    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        if (!await LoadAsync(cancellationToken) || Person is null)
        {
            return await SignInFullyAsync();
        }

        PinUnlockResult result = await _devices.UnlockAsync(Device.Id, Person.Id, Pin, cancellationToken);
        switch (result.Status)
        {
            case PinUnlockStatus.Unlocked:
                UserSummary? user = await _accounts.FindByIdAsync(result.Resume!.UserId, cancellationToken);
                if (user is null)
                {
                    return await SignInFullyAsync();
                }

                await SangamAuthentication.ResumeSessionAsync(HttpContext, user, result.Resume);
                return LocalRedirect(SafeReturnUrl(ReturnUrl));
            case PinUnlockStatus.Wrong:
                Error = result.TriesLeft == 1 ? L["Wrong PIN. One more wrong PIN and it stops working until you sign in fully."] : L["Wrong PIN. {0} tries left.", result.TriesLeft];
                break;
            case PinUnlockStatus.Cancelled:
                Error = L["Your PIN no longer works after five wrong tries. Sign in fully; it works again after that."];
                SignInOnly = true;
                break;
            case PinUnlockStatus.NoPin:
                Error = L["You have no PIN yet. Sign in fully; you can set one afterwards."];
                SignInOnly = true;
                break;
            default:
                Error = L["It has been too long since your screen was locked, or since you last signed in fully. Sign in fully."];
                SignInOnly = true;
                break;
        }

        return Page();
    }

    /// <summary>"Someone else" or "Sign in fully": the browser's session cookie goes; a locked session waits for its owner.</summary>
    /// <returns>The sign-in page.</returns>
    public Task<IActionResult> OnPostElseAsync() => SignInFullyAsync();

    private async Task<IActionResult> SignInFullyAsync()
    {
        await HttpContext.SignOutAsync(IdentityConstants.ApplicationScheme);
        return Redirect(string.IsNullOrEmpty(ReturnUrl) || !Url.IsLocalUrl(ReturnUrl) ? "/login" : "/login?returnUrl=" + Uri.EscapeDataString(ReturnUrl));
    }

    private async Task<bool> LoadAsync(CancellationToken cancellationToken)
    {
        if (await SharedDeviceCookie.CurrentAsync(HttpContext, cancellationToken) is not SharedDeviceInfo device)
        {
            return false;
        }

        Device = device;
        Guid? tileId = Tile;
        if (tileId is null && SangamAuthentication.SessionId(User) is Guid sessionId)
        {
            tileId = await _devices.LockedTileAsync(device.Id, sessionId, cancellationToken);
        }

        if (tileId is null)
        {
            return false;
        }

        IReadOnlyList<DeviceTile> tiles = await _devices.TilesAsync(device.Id, cancellationToken);
        Person = tiles.FirstOrDefault(t => t.Id == tileId);
        if (Person is null)
        {
            return false;
        }

        Tile = Person.Id;
        if (!Person.Locked)
        {
            SignInOnly = true;
            Error ??= L["It has been too long since your screen was locked, or since you last signed in fully. Sign in fully."];
        }

        return true;
    }
}
