using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Sangam.Identity.Application.Accounts;
using Sangam.Identity.Domain;
using Sangam.Identity.Domain.Enums;
using Sangam.Identity.Server.Authentication;

namespace Sangam.Identity.Server.Pages.Account;

/// <summary>A minimal signed-in page: proves the session and lets the user set their sign-in preference.</summary>
[Authorize]
public sealed class HomeModel : AuthPageModel
{
    private readonly IAccountService _accounts;

    /// <summary>Initialises the page.</summary>
    /// <param name="accounts">Account service.</param>
    public HomeModel(IAccountService accounts)
    {
        _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
    }

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
    public async Task<IActionResult> OnGetAsync(bool saved, CancellationToken cancellationToken)
    {
        if (!await LoadAsync(cancellationToken))
        {
            return RedirectToPage("/Account/Login");
        }

        if (saved)
        {
            Notice = "Your sign-in preference has been saved.";
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
            "password_and_otp" => "password and emailed code",
            "otp_only" => "emailed code",
            _ => "email and password",
        };
        SignInPreference = SignInModes.ToCode(user.SignInPreference);
        return true;
    }
}
