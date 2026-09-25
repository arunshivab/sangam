using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Sangam.Identity.Application.Accounts;
using Sangam.Identity.Application.Apps;
using Sangam.Identity.Domain.Enums;
using Sangam.Identity.Server.Authentication;
using Sangam.Identity.Server.Authorization;

namespace Sangam.Identity.Server.Pages.Account;

/// <summary>Screen 1 — sign in with email and password.</summary>
public sealed class LoginModel : AuthPageModel
{
    private readonly IAccountService _accounts;
    private readonly IAppDirectory _apps;

    /// <summary>Initialises the page.</summary>
    /// <param name="accounts">Account service.</param>
    /// <param name="apps">App directory (partner chip and sign-in policy).</param>
    public LoginModel(IAccountService accounts, IAppDirectory apps)
    {
        _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
        _apps = apps ?? throw new ArgumentNullException(nameof(apps));
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
    /// <param name="switch">Set from the consent screen's "Switch account": ends the current session first.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<IActionResult> OnGetAsync(bool signedout = false, bool reset = false, bool @switch = false, CancellationToken cancellationToken = default)
    {
        if (@switch)
        {
            await HttpContext.SignOutAsync(IdentityConstants.ApplicationScheme);
        }
        else if (User.Identity?.IsAuthenticated == true)
        {
            return LocalRedirect(SafeReturnUrl(ReturnUrl));
        }

        await ResolvePartnerAsync(_apps, ReturnUrl, cancellationToken);

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
        await ResolvePartnerAsync(_apps, ReturnUrl, cancellationToken);
        if (!ModelState.IsValid)
        {
            return Page();
        }

        SignInCheck check = await _accounts.CheckPasswordAsync(Email, Password, Partner?.SignInPolicy, ClientIp, ClientUserAgent, cancellationToken);

        switch (check.Status)
        {
            case SignInStatus.Succeeded:
                await SangamAuthentication.SignInSessionAsync(HttpContext, check.User!, check.Mode, Partner?.Id, PartnerContext.DeviceLabelFromReturnUrl(ReturnUrl));
                await _accounts.RecordSignInAsync(check.User!.Id, check.Mode, ClientIp, ClientUserAgent, cancellationToken);
                return LocalRedirect(SafeReturnUrl(ReturnUrl));

            case SignInStatus.RequiresOtp:
                await SangamAuthentication.StorePendingAsync(HttpContext, SangamAuthentication.Pending.SignInOtp, check.User!.Id, check.Mode);
                return RedirectToPage("/Account/LoginVerify", new { returnUrl = ReturnUrl });

            case SignInStatus.EmailNotVerified:
                await _accounts.IssueCodeAsync(check.User!.Id, OneTimeCodePurpose.EmailVerification, cancellationToken);
                await SangamAuthentication.StorePendingAsync(HttpContext, SangamAuthentication.Pending.EmailVerification, check.User.Id);
                return RedirectToPage("/Account/Verify", new { returnUrl = ReturnUrl });

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
