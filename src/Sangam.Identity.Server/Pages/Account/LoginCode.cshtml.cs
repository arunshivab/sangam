using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Sangam.Identity.Application.Accounts;
using Sangam.Identity.Application.Apps;
using Sangam.Identity.Domain;
using Sangam.Identity.Domain.Enums;
using Sangam.Identity.Server.Authentication;

namespace Sangam.Identity.Server.Pages.Account;

/// <summary>
/// Passwordless entry: send a code when the account uses <c>otp_only</c>. rc.11: reached from "Code by email" on the
/// "how do you want to sign in" screen with what was typed there, or directly with an email, a mobile number or a login ID.
/// </summary>
[Antibot]
public sealed class LoginCodeModel : AuthPageModel
{
    private readonly IAccountService _accounts;
    private readonly IAppDirectory _apps;

    /// <summary>Initialises the page.</summary>
    /// <param name="accounts">Account service.</param>
    /// <param name="apps">App directory.</param>
    public LoginCodeModel(IAccountService accounts, IAppDirectory apps)
    {
        _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
        _apps = apps ?? throw new ArgumentNullException(nameof(apps));
    }

    /// <summary>An email address, a mobile number or a login ID.</summary>
    [BindProperty]
    [Required(ErrorMessage = "Enter your email, mobile number or login ID.")]
    [StringLength(SignInIdentifiers.MaxLength, ErrorMessage = "Enter your email, mobile number or login ID.")]
    public string Identifier { get; set; } = string.Empty;

    /// <summary>Where to go after sign-in.</summary>
    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    /// <summary>Renders the form.</summary>
    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        if (User.Identity?.IsAuthenticated == true)
        {
            return LocalRedirect(SafeReturnUrl(ReturnUrl));
        }

        await ResolvePartnerAsync(_apps, ReturnUrl, cancellationToken);
        return Page();
    }

    /// <summary>Always moves to the code screen so the response does not reveal whether the address exists or uses codes.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        await ResolvePartnerAsync(_apps, ReturnUrl, cancellationToken);
        if (!ModelState.IsValid)
        {
            return Page();
        }

        if (SignInIdentifiers.Classify(Identifier) == SignInIdentifierKind.Invalid)
        {
            ModelState.AddModelError(nameof(Identifier), L["Enter your email, mobile number or login ID."]);
            return Page();
        }

        SignInStart start = new(Identifier.Trim(), false, null);
        return await BeginAsync(await FlowEmailAsync(start, cancellationToken), cancellationToken);
    }

    /// <summary>rc.11: "Code by email" on the "how do you want to sign in" screen, for what was typed on the first screen.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<IActionResult> OnPostFlowAsync(CancellationToken cancellationToken)
    {
        if (SignInFlow.Read(HttpContext) is not SignInStart start)
        {
            return Redirect(WithReturn("/login", ReturnUrl));
        }

        await ResolvePartnerAsync(_apps, ReturnUrl, cancellationToken);
        return await BeginAsync(await FlowEmailAsync(start, cancellationToken), cancellationToken);
    }

    private async Task<IActionResult> BeginAsync(string email, CancellationToken cancellationToken)
    {
        SignInCheck check = await _accounts.BeginOtpSignInAsync(email, Partner?.SignInPolicy, cancellationToken);
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
