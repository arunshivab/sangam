using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Sangam.Identity.Application.Devices;
using Sangam.Identity.Server.Authentication;

namespace Sangam.Identity.Server.Pages.Account;

/// <summary>
/// rc.12 (T1): on an enrolled shared device, after a full sign-in, the person may set a 6-digit PIN to come back quickly
/// after an application locks their screen. Asked once per sign-in; "Not now" is always there.
/// </summary>
public sealed class SharedDevicePinModel : AuthPageModel
{
    private readonly ISharedDeviceService _devices;

    /// <summary>Initialises the page.</summary>
    /// <param name="devices">Shared devices.</param>
    public SharedDevicePinModel(ISharedDeviceService devices)
    {
        _devices = devices ?? throw new ArgumentNullException(nameof(devices));
    }

    /// <summary>The authorization request to go back to.</summary>
    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    /// <summary>The PIN.</summary>
    [BindProperty]
    [Required(ErrorMessage = "Enter six digits.")]
    public string Pin { get; set; } = string.Empty;

    /// <summary>The PIN again.</summary>
    [BindProperty]
    public string Confirm { get; set; } = string.Empty;

    /// <summary>The device.</summary>
    public SharedDeviceInfo Device { get; private set; } = null!;

    /// <summary>Shows the offer.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The page, or back to the application when there is nothing to offer.</returns>
    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
        => await LoadAsync(cancellationToken) ? Page() : LocalRedirect(SafeReturnUrl(ReturnUrl));

    /// <summary>Sets the PIN.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Back to the application, or the page with an error.</returns>
    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        if (!await LoadAsync(cancellationToken))
        {
            return LocalRedirect(SafeReturnUrl(ReturnUrl));
        }

        if (!ModelState.IsValid)
        {
            return Page();
        }

        if (!string.Equals(Pin, Confirm, StringComparison.Ordinal))
        {
            ModelState.AddModelError(nameof(Confirm), L["The two PINs are different."]);
            return Page();
        }

        Guid userId = SangamAuthentication.UserId(User)!.Value;
        if (!await _devices.SetPinAsync(userId, Pin.Trim(), cancellationToken))
        {
            ModelState.AddModelError(nameof(Pin), L["Choose six digits that are not all the same and not a run like 123456."]);
            return Page();
        }

        await _devices.PinOfferedAsync(Device.Id, userId, SangamAuthentication.SessionId(User)!.Value, cancellationToken);
        return LocalRedirect(SafeReturnUrl(ReturnUrl));
    }

    /// <summary>"Not now": asked again at the next full sign-in here.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Back to the application.</returns>
    public async Task<IActionResult> OnPostLaterAsync(CancellationToken cancellationToken)
    {
        if (await LoadAsync(cancellationToken))
        {
            await _devices.PinOfferedAsync(Device.Id, SangamAuthentication.UserId(User)!.Value, SangamAuthentication.SessionId(User)!.Value, cancellationToken);
        }

        return LocalRedirect(SafeReturnUrl(ReturnUrl));
    }

    private async Task<bool> LoadAsync(CancellationToken cancellationToken)
    {
        if (SangamAuthentication.UserId(User) is null || SangamAuthentication.SessionId(User) is null
            || await SharedDeviceCookie.CurrentAsync(HttpContext, cancellationToken) is not SharedDeviceInfo device)
        {
            return false;
        }

        Device = device;
        return true;
    }
}
