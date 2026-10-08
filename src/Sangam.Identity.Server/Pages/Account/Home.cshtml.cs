using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Sangam.Identity.Application.Accounts;
using Sangam.Identity.Application.Passkeys;
using Sangam.Identity.Application.Sms;
using Sangam.Identity.Domain;
using Sangam.Identity.Domain.Enums;
using Sangam.Identity.Server.Authentication;

namespace Sangam.Identity.Server.Pages.Account;

/// <summary>The signed-in page on the identity server: the essentials, the sign-in preference, and the settings that must live here (mobile verification, passkeys).</summary>
[Authorize]
public sealed class HomeModel : AuthPageModel
{
    private readonly IAccountService _accounts;
    private readonly ISmsCodeService _sms;
    private readonly IPasskeyService _passkeys;

    /// <summary>Initialises the page.</summary>
    /// <param name="accounts">Account service.</param>
    /// <param name="sms">SMS codes, to offer mobile verification.</param>
    /// <param name="passkeys">Passkeys, to offer managing them.</param>
    public HomeModel(IAccountService accounts, ISmsCodeService sms, IPasskeyService passkeys)
    {
        _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
        _sms = sms ?? throw new ArgumentNullException(nameof(sms));
        _passkeys = passkeys ?? throw new ArgumentNullException(nameof(passkeys));
    }

    /// <summary>Whether the mobile can be verified by SMS here.</summary>
    public bool OfferMobileVerification => _sms.Enabled && !User.MobileVerified && _sms.IsCountryAllowed(User.Mobile);

    /// <summary>Whether passkeys are offered.</summary>
    public bool OfferPasskeys => _passkeys.Enabled;

    /// <summary>The signed-in user.</summary>
    public new UserSummary User { get; private set; } = null!;

    /// <summary>Mode the session was established with.</summary>
    public string SessionMode { get; private set; } = string.Empty;

    /// <summary>Selected preference (snake_case).</summary>
    [BindProperty]
    public string SignInPreference { get; set; } = "password";

    /// <summary>Notice after saving.</summary>
    public string? Notice { get; private set; }

    /// <summary>Renders the page.</summary>
    /// <param name="saved">The preference was just saved.</param>
    /// <param name="passwordChanged">The password was just changed (R7).</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public async Task<IActionResult> OnGetAsync(bool saved, bool passwordChanged, CancellationToken cancellationToken)
    {
        if (!await LoadAsync(cancellationToken))
        {
            return RedirectToPage("/Account/Login");
        }

        if (saved)
        {
            Notice = L["Your sign-in preference has been saved."];
        }
        else if (passwordChanged)
        {
            Notice = L["Your password is changed. Your other devices are being signed out."];
        }

        return Page();
    }

    /// <summary>Saves the sign-in preference.</summary>
    public async Task<IActionResult> OnPostPreferenceAsync(CancellationToken cancellationToken)
    {
        string posted = SignInPreference;
        if (!await LoadAsync(cancellationToken))
        {
            return RedirectToPage("/Account/Login");
        }

        if (!SignInModes.TryParse(posted, out SignInMode mode))
        {
            return Page();
        }

        await _accounts.SetSignInPreferenceAsync(User.Id, mode, cancellationToken);
        return RedirectToPage("/Account/Home", new { saved = true });
    }

    private async Task<bool> LoadAsync(CancellationToken cancellationToken)
    {
        Guid? id = SangamAuthentication.UserId(base.User);
        UserSummary? user = id is null ? null : await _accounts.FindByIdAsync(id.Value, cancellationToken);
        if (user is null)
        {
            return false;
        }

        User = user;
        SessionMode = base.User.FindFirst(SangamAuthentication.SessionModeClaim)?.Value switch
        {
            "password_and_otp" => L["password and emailed code"],
            "otp_only" => L["emailed code"],
            _ => L["email and password"],
        };
        SignInPreference = SignInModes.ToCode(user.SignInPreference);
        return true;
    }
}
