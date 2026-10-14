using Microsoft.AspNetCore.Mvc;
using Sangam.Identity.Application.Devices;
using Sangam.Identity.Server.Authentication;

namespace Sangam.Identity.Server.Pages.Account;

/// <summary>
/// rc.12 (T1): the last step of enrolling a shared ward device, opened on the device itself from the organisation console's
/// one-time link (15 minutes), signed in as the admin who started it. The device's key is then kept in this browser.
/// </summary>
public sealed class SharedDeviceEnrolModel : AuthPageModel
{
    private readonly ISharedDeviceService _devices;

    /// <summary>Initialises the page.</summary>
    /// <param name="devices">Shared devices.</param>
    public SharedDeviceEnrolModel(ISharedDeviceService devices)
    {
        _devices = devices ?? throw new ArgumentNullException(nameof(devices));
    }

    /// <summary>The one-time token from the organisation console.</summary>
    [BindProperty(SupportsGet = true)]
    public string? Token { get; set; }

    /// <summary>The label, once enrolled.</summary>
    public string? Enrolled { get; private set; }

    /// <summary>Whether the link did not work.</summary>
    public bool Failed { get; private set; }

    /// <summary>Asks to finish (signing in first if needed).</summary>
    /// <returns>The page.</returns>
    public IActionResult OnGet()
    {
        if (SangamAuthentication.UserId(User) is null)
        {
            return Redirect("/login?returnUrl=" + Uri.EscapeDataString("/shared-device/enrol?token=" + Uri.EscapeDataString(Token ?? string.Empty)));
        }

        return Page();
    }

    /// <summary>Finishes enrolment: the key is kept in this browser.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The page.</returns>
    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        Guid? userId = SangamAuthentication.UserId(User);
        if (userId is null)
        {
            return OnGet();
        }

        DeviceEnrolled? enrolled = await _devices.CompleteEnrolAsync(Token ?? string.Empty, userId.Value, cancellationToken);
        if (enrolled is null)
        {
            Failed = true;
            return Page();
        }

        SharedDeviceCookie.Write(HttpContext, enrolled.Key);
        Enrolled = enrolled.Label;
        return Page();
    }
}
