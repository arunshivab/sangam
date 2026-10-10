using Microsoft.AspNetCore.Mvc;
using Sangam.Identity.Infrastructure.Verification;
using Sangam.Identity.Server.Authentication;
using Sangam.Identity.Server.Verification;

namespace Sangam.Identity.Server.Pages.Account;

/// <summary>
/// rc.6 (SGM-914 section 5): a person who has passed their first step but lost their authenticator and recovery codes
/// starts a recovery with DigiLocker. Reached only with a pending first step, so it never reveals whether an account exists.
/// </summary>
public sealed class RecoverModel : AuthPageModel
{
    private readonly DigiLockerClient _digiLocker;

    /// <summary>Initialises the page.</summary>
    /// <param name="digiLocker">DigiLocker.</param>
    public RecoverModel(DigiLockerClient digiLocker)
    {
        _digiLocker = digiLocker ?? throw new ArgumentNullException(nameof(digiLocker));
    }

    /// <summary>Whether DigiLocker is switched on.</summary>
    public bool Available => _digiLocker.Enabled;

    /// <summary>Explains the recovery, or returns to sign-in when there is no pending first step.</summary>
    public async Task<IActionResult> OnGetAsync()
    {
        PendingFlow? pending = await SangamAuthentication.ReadPendingAsync(HttpContext, SangamAuthentication.Pending.Authenticator);
        return pending?.UserId is null ? RedirectToPage("/Account/Login") : Page();
    }

    /// <summary>Sends the person to DigiLocker.</summary>
    public async Task<IActionResult> OnPostAsync()
    {
        PendingFlow? pending = await SangamAuthentication.ReadPendingAsync(HttpContext, SangamAuthentication.Pending.Authenticator);
        if (pending?.UserId is not Guid userId)
        {
            return RedirectToPage("/Account/Login");
        }

        if (!Available || Request.Form["agree"] != "true")
        {
            return RedirectToPage();
        }

        return Redirect(IdentityDigiLocker.Start(HttpContext, IdentityDigiLocker.Recover, userId, null));
    }
}
