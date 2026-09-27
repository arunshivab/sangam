using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Sangam.Identity.Application.Accounts;
using Sangam.Identity.Application.Admin;
using Sangam.Identity.Application.Apps;
using Sangam.Identity.Domain.Enums;
using Sangam.Identity.Server.Authentication;
using Sangam.Identity.Server.Authorization;

namespace Sangam.Identity.Server.Pages.Account;

/// <summary>
/// The authenticator step. Reached only once a first factor has already been accepted and
/// parked in the pending cookie, so a wrong code here never reveals whether the password was
/// right.
/// </summary>
public sealed class LoginMfaModel : AuthPageModel
{
    private readonly IAccountService _accounts;
    private readonly IAppDirectory _apps;
    private readonly IMfaService _mfa;

    /// <summary>Initialises the page.</summary>
    /// <param name="accounts">Account service.</param>
    /// <param name="apps">App directory, for the partner chip.</param>
    /// <param name="mfa">Second-factor service.</param>
    public LoginMfaModel(IAccountService accounts, IAppDirectory apps, IMfaService mfa)
    {
        _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
        _apps = apps ?? throw new ArgumentNullException(nameof(apps));
        _mfa = mfa ?? throw new ArgumentNullException(nameof(mfa));
    }

    /// <summary>Where to continue after the code is accepted.</summary>
    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    /// <summary>The code typed by the user.</summary>
    [BindProperty]
    [Required(ErrorMessage = "Enter the code from your authenticator app.")]
    public string Code { get; set; } = string.Empty;

    /// <summary>Banner text.</summary>
    public string? Error { get; private set; }

    /// <summary>Renders the form, or returns to sign-in when there is no pending first factor.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        PendingFlow? pending = await SangamAuthentication.ReadPendingAsync(HttpContext, SangamAuthentication.Pending.Authenticator);
        if (pending is null)
        {
            return RedirectToPage("/Account/Login", new { returnUrl = ReturnUrl });
        }

        await ResolvePartnerAsync(_apps, ReturnUrl, cancellationToken);
        return Page();
    }

    /// <summary>Checks the code and completes the sign-in.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        PendingFlow? pending = await SangamAuthentication.ReadPendingAsync(HttpContext, SangamAuthentication.Pending.Authenticator);
        if (pending?.UserId is not Guid userId)
        {
            return RedirectToPage("/Account/Login", new { returnUrl = ReturnUrl });
        }

        await ResolvePartnerAsync(_apps, ReturnUrl, cancellationToken);
        if (!ModelState.IsValid)
        {
            return Page();
        }

        MfaResult result = await _mfa.VerifyAsync(userId, Code, ClientIp, cancellationToken);
        if (result is MfaResult.LockedOut)
        {
            await SangamAuthentication.ClearPendingAsync(HttpContext);
            Error = "Too many incorrect codes. Try again in 15 minutes.";
            return Page();
        }

        if (result is not MfaResult.Valid)
        {
            Error = "That code is not correct. Check the app and try again.";
            return Page();
        }

        UserSummary? user = await _accounts.FindByIdAsync(userId, cancellationToken);
        if (user is null)
        {
            await SangamAuthentication.ClearPendingAsync(HttpContext);
            return RedirectToPage("/Account/Login");
        }

        SignInMode mode = pending.Mode ?? SignInMode.Password;
        await SangamAuthentication.ClearPendingAsync(HttpContext);
        await SangamAuthentication.SignInSessionAsync(HttpContext, user, mode, Partner?.Id, PartnerContext.DeviceLabelFromReturnUrl(ReturnUrl));
        await _accounts.RecordSignInAsync(user.Id, mode, ClientIp, ClientUserAgent, cancellationToken);
        return LocalRedirect(SafeReturnUrl(ReturnUrl));
    }
}
