using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Sangam.Identity.Application.Accounts;
using Sangam.Identity.Domain.Enums;
using Sangam.Identity.Server.Authentication;

namespace Sangam.Identity.Server.Pages.Account;

/// <summary>Passwordless entry: ask for the address, send a code when the account uses <c>otp_only</c>.</summary>
public sealed class LoginCodeModel : AuthPageModel
{
    private readonly IAccountService _accounts;

    /// <summary>Initialises the page.</summary>
    /// <param name="accounts">Account service.</param>
    public LoginCodeModel(IAccountService accounts)
    {
        _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
    }

    /// <summary>Email address.</summary>
    [BindProperty]
    [Required(ErrorMessage = "Enter your email address.")]
    [EmailAddress(ErrorMessage = "Enter a valid email address.")]
    public string Email { get; set; } = string.Empty;

    /// <summary>Where to go after sign-in.</summary>
    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    /// <summary>Renders the form.</summary>
    public IActionResult OnGet() => User.Identity?.IsAuthenticated == true ? LocalRedirect(SafeReturnUrl(ReturnUrl)) : Page();

    /// <summary>Always moves to the code screen so the response does not reveal whether the address exists or uses codes.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return Page();
        }

        SignInCheck check = await _accounts.BeginOtpSignInAsync(Email, appPolicy: null, cancellationToken);
        if (check.Status == SignInStatus.RequiresOtp && check.User is not null)
        {
            await SangamAuthentication.StorePendingAsync(HttpContext, SangamAuthentication.Pending.SignInOtp, check.User.Id, check.Mode);
        }
        else
        {
            // Unknown address, or an account that does not use codes: the next screen looks identical and every code fails.
            await SangamAuthentication.StorePendingAsync(HttpContext, SangamAuthentication.Pending.SignInOtp, Guid.Empty, SignInMode.OtpOnly);
        }

        return RedirectToPage("/Account/LoginVerify", new { returnUrl = ReturnUrl });
    }
}
