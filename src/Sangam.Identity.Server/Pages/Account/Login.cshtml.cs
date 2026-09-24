using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Sangam.Identity.Application.Accounts;
using Sangam.Identity.Domain.Enums;
using Sangam.Identity.Server.Authentication;

namespace Sangam.Identity.Server.Pages.Account;

/// <summary>Screen 1 — sign in with email and password.</summary>
public sealed class LoginModel : AuthPageModel
{
    private readonly IAccountService _accounts;

    /// <summary>Initialises the page.</summary>
    /// <param name="accounts">Account service.</param>
    public LoginModel(IAccountService accounts)
    {
        _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
    }

    /// <summary>Email address.</summary>
    [BindProperty]
    [Required(ErrorMessage = "Enter your email address.")]
    [EmailAddress(ErrorMessage = "Enter a valid email address.")]
    public string Email { get; set; } = string.Empty;

    /// <summary>Password.</summary>
    [BindProperty]
    [Required(ErrorMessage = "Enter your password.")]
    public string Password { get; set; } = string.Empty;

    /// <summary>Where to go after sign-in (local URLs only).</summary>
    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    /// <summary>Page-level error.</summary>
    public string? Error { get; private set; }

    /// <summary>Page-level notice (after sign-out or a password reset).</summary>
    public string? Notice { get; private set; }

    /// <summary>Renders the form.</summary>
    /// <param name="signedout">Set after sign-out.</param>
    /// <param name="reset">Set after a password reset.</param>
    public IActionResult OnGet(bool signedout = false, bool reset = false)
    {
        if (User.Identity?.IsAuthenticated == true)
        {
            return LocalRedirect(SafeReturnUrl(ReturnUrl));
        }

        if (signedout)
        {
            Notice = "You have been signed out.";
        }
        else if (reset)
        {
            Notice = "Your password has been updated. Sign in with the new one.";
        }

        return Page();
    }

    /// <summary>Checks the credentials and either signs in, asks for a code, or shows why not.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return Page();
        }

        SignInCheck check = await _accounts.CheckPasswordAsync(Email, Password, appPolicy: null, ClientIp, ClientUserAgent, cancellationToken);

        switch (check.Status)
        {
            case SignInStatus.Succeeded:
                await SangamAuthentication.SignInSessionAsync(HttpContext, check.User!, check.Mode);
                await _accounts.RecordSignInAsync(check.User!.Id, check.Mode, ClientIp, ClientUserAgent, cancellationToken);
                return LocalRedirect(SafeReturnUrl(ReturnUrl));

            case SignInStatus.RequiresOtp:
                await SangamAuthentication.StorePendingAsync(HttpContext, SangamAuthentication.Pending.SignInOtp, check.User!.Id, check.Mode);
                return RedirectToPage("/Account/LoginVerify", new { returnUrl = ReturnUrl });

            case SignInStatus.EmailNotVerified:
                await _accounts.IssueCodeAsync(check.User!.Id, OneTimeCodePurpose.EmailVerification, cancellationToken);
                await SangamAuthentication.StorePendingAsync(HttpContext, SangamAuthentication.Pending.EmailVerification, check.User.Id);
                return RedirectToPage("/Account/Verify");

            case SignInStatus.LockedOut:
                Error = "Too many failed attempts. Try again in 15 minutes, or reset your password.";
                return Page();

            case SignInStatus.NotAllowed:
                Error = "This account cannot sign in. Contact help@sangamid.in if you think this is a mistake.";
                return Page();

            default:
                Error = "That email and password do not match.";
                return Page();
        }
    }
}
