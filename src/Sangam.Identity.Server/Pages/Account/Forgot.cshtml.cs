using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Sangam.Identity.Application.Accounts;
using Sangam.Identity.Server.Authentication;

namespace Sangam.Identity.Server.Pages.Account;

/// <summary>Screen 5 — forgot password. No partner chip; the response never reveals whether the address exists.</summary>
public sealed class ForgotModel : AuthPageModel
{
    private readonly IAccountService _accounts;

    /// <summary>Initialises the page.</summary>
    /// <param name="accounts">Account service.</param>
    public ForgotModel(IAccountService accounts)
    {
        _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
    }

    /// <summary>Email.</summary>
    [BindProperty]
    [Required(ErrorMessage = "Enter your email address.")]
    [EmailAddress(ErrorMessage = "Enter a valid email address.")]
    public string Email { get; set; } = string.Empty;

    /// <summary>Renders the form.</summary>
    public void OnGet()
    {
    }

    /// <summary>Sends a code when the account exists and always continues to the reset screen.</summary>
    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return Page();
        }

        await _accounts.RequestPasswordResetAsync(Email, ClientIp, cancellationToken);
        await SangamAuthentication.StorePendingResetAsync(HttpContext, Email.Trim());
        return RedirectToPage("/Account/Reset");
    }
}
