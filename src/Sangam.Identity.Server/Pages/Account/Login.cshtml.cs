using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Sangam.Identity.Application.Accounts;
using Sangam.Identity.Application.Apps;
using Sangam.Identity.Application.Passkeys;
using Sangam.Identity.Application.Security;
using Sangam.Identity.Domain;
using Sangam.Identity.Domain.Enums;
using Sangam.Identity.Server.Authentication;

namespace Sangam.Identity.Server.Pages.Account;

/// <summary>Screen 1 — sign in with email and password.</summary>
[Antibot]
public sealed class LoginModel : AuthPageModel
{
    private readonly IAccountService _accounts;
    private readonly IAppDirectory _apps;
    private readonly IPasskeyService _passkeys;
    private readonly ISecurityPolicyService _policies;
    private readonly IBreachedPasswordChecker _breaches;

    /// <summary>Initialises the page.</summary>
    /// <param name="accounts">Account service.</param>
    /// <param name="apps">App directory (partner chip and sign-in policy).</param>
    /// <param name="passkeys">Passkeys (PR-14).</param>
    /// <param name="policies">Security policies (PR-16).</param>
    /// <param name="breaches">Breached-password check (PR-16).</param>
    public LoginModel(IAccountService accounts, IAppDirectory apps, IPasskeyService passkeys, ISecurityPolicyService policies, IBreachedPasswordChecker breaches)
    {
        _passkeys = passkeys ?? throw new ArgumentNullException(nameof(passkeys));
        _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
        _apps = apps ?? throw new ArgumentNullException(nameof(apps));
        _policies = policies ?? throw new ArgumentNullException(nameof(policies));
        _breaches = breaches ?? throw new ArgumentNullException(nameof(breaches));
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

    /// <summary>Whether to offer passkey sign-in (PR-14).</summary>
    public bool PasskeysEnabled => _passkeys.Enabled;

    /// <summary>Whether the application in the flow accepts only passkeys (PR-16): the password form is not offered.</summary>
    public bool PasskeyOnly => (Partner?.SignInPolicy == SignInPolicy.PasskeyOnly || RequiredLevel(ReturnUrl) >= 3) && _passkeys.Enabled;

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
            Notice = L["You have been signed out."];
        }
        else if (reset)
        {
            Notice = L["Your password has been updated. Sign in with the new one."];
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
            case SignInStatus.Succeeded or SignInStatus.RequiresOtp:
                return await ApplyPolicyAsync(check.User!, check.Mode, cancellationToken);

            case SignInStatus.EmailNotVerified:
                await _accounts.IssueCodeAsync(check.User!.Id, OneTimeCodePurpose.EmailVerification, cancellationToken);
                await SangamAuthentication.StorePendingAsync(HttpContext, SangamAuthentication.Pending.EmailVerification, check.User.Id);
                return RedirectToPage("/Account/Verify", new { returnUrl = ReturnUrl });

            case SignInStatus.LockedOut:
                Error = L["Too many failed attempts. Try again in 15 minutes, or reset your password."];
                return Page();

            case SignInStatus.NotAllowed:
                Error = L["This account cannot sign in. Contact help@sangamid.in if you think this is a mistake."];
                return Page();

            default:
                Error = L["That email and password do not match."];
                return Page();
        }
    }

    /// <summary>
    /// The password is right. Applies the person's policy in this application (PR-16): a passkey-only rule refuses
    /// the password; an organisation that requires two-step adds the code step; a password too short for the policy,
    /// or found in a breach, must be replaced before the sign-in goes on.
    /// </summary>
    private async Task<IActionResult> ApplyPolicyAsync(UserSummary user, SignInMode mode, CancellationToken cancellationToken)
    {
        PersonPolicy person = await _policies.ForPersonAsync(user.Id, Partner?.Id, cancellationToken);
        int stepUp = RequiredLevel(ReturnUrl);
        if (person.Policy.SignIn == SignInPolicy.PasskeyOnly || mode == SignInMode.Passkey || stepUp >= 3)
        {
            Error = L["{0} requires you to sign in with a passkey. Use “Sign in with a passkey”, or add one to your account first.", Partner?.DisplayName ?? L["This application"]];
            return Page();
        }

        SignInMode required = SignInModes.Resolve(person.Policy.SignIn, user.SignInPreference);
        // An organisation's two-step rule, or an application's step-up request (PR-17) that the person's
        // authenticator will not already satisfy, adds the code step here.
        bool stepUpNeedsCode = stepUp >= 2 && !user.MfaEnrolled && mode is SignInMode.Password;
        if ((required == SignInMode.PasswordAndOtp || stepUpNeedsCode) && mode != SignInMode.PasswordAndOtp)
        {
            // An organisation tightened the application's rule: the code step is added here.
            await _accounts.IssueCodeAsync(user.Id, OneTimeCodePurpose.SignIn, cancellationToken);
            mode = SignInMode.PasswordAndOtp;
        }

        string? reason = Password.Length < person.Policy.MinPasswordLength
            ? "too_short"
            : person.Policy.BreachedPasswordCheck && await _breaches.IsBreachedAsync(Password, cancellationToken) == true ? "breached" : null;
        if (reason is not null)
        {
            await SangamAuthentication.StorePendingAsync(HttpContext, SangamAuthentication.Pending.PasswordUpgrade, user.Id, mode);
            return RedirectToPage("/Account/LoginNewPassword", new { returnUrl = ReturnUrl, reason });
        }

        return await ContinueAfterPasswordAsync(_accounts, user, mode, ReturnUrl, cancellationToken);
    }
}
