using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Sangam.Identity.Application.Accounts;
using Sangam.Identity.Application.Security;
using Sangam.Identity.Server.Authentication;

namespace Sangam.Identity.Server.Pages.Account;

/// <summary>
/// R7 (ASVS V2.1.6): a signed-in person changes their password, giving the current one first. Other devices are signed
/// out; this one stays signed in; the person is told by e-mail.
/// </summary>
[Authorize]
public sealed class PasswordModel : PageModel
{
    private readonly IAccountService _accounts;
    private readonly ISecurityPolicyService _policies;

    /// <summary>Initialises the page.</summary>
    /// <param name="accounts">Accounts.</param>
    /// <param name="policies">Security policies.</param>
    public PasswordModel(IAccountService accounts, ISecurityPolicyService policies)
    {
        _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
        _policies = policies ?? throw new ArgumentNullException(nameof(policies));
    }

    /// <summary>The current password.</summary>
    [BindProperty]
    [Required(ErrorMessage = "Enter your current password.")]
    public string CurrentPassword { get; set; } = string.Empty;

    /// <summary>New password.</summary>
    [BindProperty]
    [Required(ErrorMessage = "Choose a new password.")]
    public string NewPassword { get; set; } = string.Empty;

    /// <summary>Confirmation.</summary>
    [BindProperty]
    [Required(ErrorMessage = "Type the new password again.")]
    public string ConfirmPassword { get; set; } = string.Empty;

    /// <summary>The shortest password the person's policies allow.</summary>
    public int MinimumLength { get; private set; } = PasswordStrength.MinimumLength;

    /// <summary>Strength of the new password.</summary>
    public PasswordStrengthResult Strength { get; private set; } = PasswordStrength.Evaluate(null);

    /// <summary>The text catalogue, in the request's language.</summary>
    private Microsoft.Extensions.Localization.IStringLocalizer L => PageText.For(HttpContext);

    /// <summary>Shows the form.</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        MinimumLength = (await _policies.ForPasswordAsync(UserId(), cancellationToken)).MinPasswordLength;
        return Page();
    }

    /// <summary>Changes the password.</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        Guid userId = UserId();
        MinimumLength = (await _policies.ForPasswordAsync(userId, cancellationToken)).MinPasswordLength;
        Strength = PasswordStrength.Evaluate(NewPassword);
        if (NewPassword.Length < MinimumLength)
        {
            ModelState.AddModelError(nameof(NewPassword), L["Use at least {0} characters.", MinimumLength]);
        }

        if (!string.Equals(NewPassword, ConfirmPassword, StringComparison.Ordinal))
        {
            ModelState.AddModelError(nameof(ConfirmPassword), L["The two passwords do not match."]);
        }

        if (!ModelState.IsValid)
        {
            return Page();
        }

        AccountResult result = await _accounts.ChangePasswordAsync(userId, CurrentPassword, NewPassword, HttpContext.Connection.RemoteIpAddress?.ToString(), cancellationToken);
        if (!result.Succeeded)
        {
            foreach (AccountError error in result.Errors)
            {
                ModelState.AddModelError(error.Field == nameof(CurrentPassword) ? nameof(CurrentPassword) : nameof(NewPassword), L[error.Message ?? string.Empty]);
            }

            return Page();
        }

        UserSummary? user = await _accounts.FindByIdAsync(userId, cancellationToken);
        if (user is not null)
        {
            await SangamAuthentication.RenewStampAsync(HttpContext, user.SecurityStamp);
        }

        return RedirectToPage("/Account/Home", new { passwordChanged = true });
    }

    private Guid UserId() => SangamAuthentication.UserId(User) ?? throw new InvalidOperationException("Signed-in user has no id.");
}
