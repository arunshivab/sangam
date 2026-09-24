using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Sangam.Identity.Application.Accounts;
using Sangam.Identity.Application.Security;
using Sangam.Identity.Server.Authentication;

namespace Sangam.Identity.Server.Pages.Account;

/// <summary>Screen 6 — reset password with the emailed code.</summary>
public sealed class ResetModel : AuthPageModel
{
    private readonly IAccountService _accounts;

    /// <summary>Initialises the page.</summary>
    /// <param name="accounts">Account service.</param>
    public ResetModel(IAccountService accounts)
    {
        _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
    }

    /// <summary>Email the code went to (from the pending cookie, then the hidden field).</summary>
    [BindProperty]
    [Required]
    [EmailAddress]
    public string Email { get; set; } = string.Empty;

    /// <summary>Reset code.</summary>
    [BindProperty]
    [Required(ErrorMessage = "Enter the 6-digit code from the email.")]
    public string Code { get; set; } = string.Empty;

    /// <summary>New password.</summary>
    [BindProperty]
    [Required(ErrorMessage = "Choose a new password.")]
    public string NewPassword { get; set; } = string.Empty;

    /// <summary>Confirmation.</summary>
    [BindProperty]
    [Required(ErrorMessage = "Type the new password again.")]
    public string ConfirmPassword { get; set; } = string.Empty;

    /// <summary>Strength of the new password.</summary>
    public PasswordStrengthResult Strength { get; private set; } = PasswordStrength.Evaluate(null);

    /// <summary>Page-level error.</summary>
    public string? Error { get; private set; }

    /// <summary>Renders the form; without a pending reset, sends the user to the forgot screen.</summary>
    public async Task<IActionResult> OnGetAsync()
    {
        PendingFlow? pending = await SangamAuthentication.ReadPendingAsync(HttpContext, SangamAuthentication.Pending.PasswordReset);
        if (pending?.Email is null)
        {
            return RedirectToPage("/Account/Forgot");
        }

        Email = pending.Email;
        return Page();
    }

    /// <summary>Sets the new password when the code is valid.</summary>
    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        Strength = PasswordStrength.Evaluate(NewPassword);

        if (!string.Equals(NewPassword, ConfirmPassword, StringComparison.Ordinal))
        {
            ModelState.AddModelError(nameof(ConfirmPassword), "The passwords do not match.");
        }

        if (!ModelState.IsValid)
        {
            return Page();
        }

        AccountResult result = await _accounts.ResetPasswordAsync(Email, Code, NewPassword, ClientIp, cancellationToken);
        if (!result.Succeeded)
        {
            foreach (AccountError error in result.Errors)
            {
                if (error.Field is null)
                {
                    Error = error.Message;
                }
                else
                {
                    ModelState.AddModelError(error.Field, error.Message);
                }
            }

            return Page();
        }

        await SangamAuthentication.ClearPendingAsync(HttpContext);
        await HttpContext.SignOutAsync(IdentityConstants.ApplicationScheme);
        return RedirectToPage("/Account/Login", new { reset = true });
    }
}
