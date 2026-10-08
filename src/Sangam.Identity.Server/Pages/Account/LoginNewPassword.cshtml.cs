using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Sangam.Identity.Application.Accounts;
using Sangam.Identity.Application.Apps;
using Sangam.Identity.Application.Security;
using Sangam.Identity.Domain.Enums;
using Sangam.Identity.Server.Authentication;

namespace Sangam.Identity.Server.Pages.Account;

/// <summary>
/// A sign-in step (PR-16): the password was right, but a policy of this application or of the person's
/// organisation no longer accepts it — too short, or found in a breach. A new one is chosen, then the sign-in goes on.
/// </summary>
public sealed class LoginNewPasswordModel : AuthPageModel
{
    private readonly IAccountService _accounts;
    private readonly IAppDirectory _apps;
    private readonly ISecurityPolicyService _policies;

    /// <summary>Initialises the page.</summary>
    /// <param name="accounts">Accounts.</param>
    /// <param name="apps">App directory.</param>
    /// <param name="policies">Security policies.</param>
    public LoginNewPasswordModel(IAccountService accounts, IAppDirectory apps, ISecurityPolicyService policies)
    {
        _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
        _apps = apps ?? throw new ArgumentNullException(nameof(apps));
        _policies = policies ?? throw new ArgumentNullException(nameof(policies));
    }

    /// <summary>Where to go after sign-in.</summary>
    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    /// <summary>Why the password must change: <c>too_short</c> or <c>breached</c>.</summary>
    [BindProperty(SupportsGet = true)]
    public string? Reason { get; set; }

    /// <summary>New password.</summary>
    [BindProperty]
    [Required(ErrorMessage = "Choose a new password.")]
    public string NewPassword { get; set; } = string.Empty;

    /// <summary>Confirmation.</summary>
    [BindProperty]
    [Required(ErrorMessage = "Type the new password again.")]
    public string ConfirmPassword { get; set; } = string.Empty;

    /// <summary>The shortest password the policy allows.</summary>
    public int MinimumLength { get; private set; } = PasswordStrength.MinimumLength;

    /// <summary>Strength of the new password.</summary>
    public PasswordStrengthResult Strength { get; private set; } = PasswordStrength.Evaluate(null);

    /// <summary>Shows the form, or sends the person back to sign-in when there is no pending step.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        PendingFlow? pending = await SangamAuthentication.ReadPendingAsync(HttpContext, SangamAuthentication.Pending.PasswordUpgrade);
        if (pending?.UserId is not Guid userId)
        {
            return RedirectToPage("/Account/Login", new { returnUrl = ReturnUrl });
        }

        await PrepareAsync(userId, cancellationToken);
        return Page();
    }

    /// <summary>Sets the new password and continues the sign-in.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        PendingFlow? pending = await SangamAuthentication.ReadPendingAsync(HttpContext, SangamAuthentication.Pending.PasswordUpgrade);
        if (pending?.UserId is not Guid userId)
        {
            return RedirectToPage("/Account/Login", new { returnUrl = ReturnUrl });
        }

        await PrepareAsync(userId, cancellationToken);
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

        AccountResult result = await _accounts.ReplacePasswordAsync(userId, NewPassword, Reason == "breached" ? "breached" : "too_short", ClientIp, cancellationToken);
        if (!result.Succeeded)
        {
            foreach (AccountError error in result.Errors)
            {
                ModelState.AddModelError(nameof(NewPassword), L[error.Message ?? string.Empty]);
            }

            return Page();
        }

        // Fresh, so the session carries the new security stamp.
        UserSummary? user = await _accounts.FindByIdAsync(userId, cancellationToken);
        if (user is null)
        {
            return RedirectToPage("/Account/Login");
        }

        return await ContinueAfterPasswordAsync(_accounts, user, pending.Mode ?? SignInMode.Password, ReturnUrl, cancellationToken);
    }

    private async Task PrepareAsync(Guid userId, CancellationToken cancellationToken)
    {
        await ResolvePartnerAsync(_apps, ReturnUrl, cancellationToken);
        // The account-wide rules, and this application's — which may not count yet, if this is the person's
        // first sign-in to it and they are not linked to it.
        int everywhere = (await _policies.ForPasswordAsync(userId, cancellationToken)).MinPasswordLength;
        int here = (await _policies.ForPersonAsync(userId, Partner?.Id, cancellationToken)).Policy.MinPasswordLength;
        MinimumLength = Math.Max(everywhere, here);
    }
}
