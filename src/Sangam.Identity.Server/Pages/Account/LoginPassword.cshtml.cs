using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Sangam.Identity.Application.Abstractions;
using Sangam.Identity.Application.Accounts;
using Sangam.Identity.Application.Apps;
using Sangam.Identity.Application.Security;
using Sangam.Identity.Domain;
using Sangam.Identity.Domain.Enums;
using Sangam.Identity.Server.Authentication;

namespace Sangam.Identity.Server.Pages.Account;

/// <summary>
/// Screen 3 (rc.11, A3): the password, for the email, mobile number or login ID typed on the first screen. What follows is
/// unchanged from the one-screen sign-in: the application's and the organisation's rules, the code or authenticator step,
/// a password that must be replaced. A name with no account gets the same answer, in the same time, as a wrong password.
/// </summary>
[Antibot]
public sealed class LoginPasswordModel : AuthPageModel
{
    private readonly IAccountService _accounts;
    private readonly IAppDirectory _apps;
    private readonly ISecurityPolicyService _policies;
    private readonly IBreachedPasswordChecker _breaches;
    private readonly IDataProtectionProvider _protection;
    private readonly IClock _clock;

    /// <summary>Initialises the page.</summary>
    /// <param name="accounts">Account service.</param>
    /// <param name="apps">App directory (partner chip and sign-in policy).</param>
    /// <param name="policies">Security policies (PR-16).</param>
    /// <param name="breaches">Breached-password check (PR-16).</param>
    /// <param name="protection">Data protection, for the bot-check token.</param>
    /// <param name="clock">Clock.</param>
    public LoginPasswordModel(IAccountService accounts, IAppDirectory apps, ISecurityPolicyService policies, IBreachedPasswordChecker breaches, IDataProtectionProvider protection, IClock clock)
    {
        _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
        _apps = apps ?? throw new ArgumentNullException(nameof(apps));
        _policies = policies ?? throw new ArgumentNullException(nameof(policies));
        _breaches = breaches ?? throw new ArgumentNullException(nameof(breaches));
        _protection = protection ?? throw new ArgumentNullException(nameof(protection));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <summary>Password.</summary>
    [BindProperty]
    [Required(ErrorMessage = "Enter your password.")]
    public string Password { get; set; } = string.Empty;

    /// <summary>Where to go after sign-in (local URLs only).</summary>
    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    /// <summary>Page-level error.</summary>
    public string? Error { get; private set; }

    /// <summary>What the first screen remembered.</summary>
    public SignInStart Start { get; private set; } = new(string.Empty, false, null);

    /// <summary>The bot-check token the form posts with (the first screen's, so a password manager's quick fill is not refused).</summary>
    public string FormToken { get; private set; } = string.Empty;

    /// <summary>The username the browser's password manager files the password under.</summary>
    public string UsernameHint => Start.PickedUserId is null ? Start.Identifier : string.Empty;

    /// <summary>Renders the form.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        if (User.Identity?.IsAuthenticated == true)
        {
            return LocalRedirect(SafeReturnUrl(ReturnUrl));
        }

        if (!await LoadAsync(cancellationToken))
        {
            return Redirect(WithReturn("/login", ReturnUrl));
        }

        return Page();
    }

    /// <summary>Checks the password and either signs in, asks for the next step, or shows why not.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        if (!await LoadAsync(cancellationToken))
        {
            return Redirect(WithReturn("/login", ReturnUrl));
        }

        if (!ModelState.IsValid)
        {
            return Page();
        }

        string email = await FlowEmailAsync(Start, cancellationToken);
        SignInCheck check = await _accounts.CheckPasswordAsync(email, Password, Partner?.SignInPolicy, ClientIp, ClientUserAgent, cancellationToken);

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
                Error = L["That password does not match. Check what you typed, or use “Forgot your password?”."];
                return Page();
        }
    }

    private async Task<bool> LoadAsync(CancellationToken cancellationToken)
    {
        if (SignInFlow.Read(HttpContext) is not SignInStart start)
        {
            return false;
        }

        Start = start;
        FormToken = string.IsNullOrEmpty(start.FormToken) ? Antibot.CreateToken(_protection, _clock.UtcNow) : start.FormToken;
        await ResolvePartnerAsync(_apps, ReturnUrl, cancellationToken);
        ExposeReturnQuery(ReturnUrl);
        return true;
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
