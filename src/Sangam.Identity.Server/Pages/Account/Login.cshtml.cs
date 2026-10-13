using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Sangam.Identity.Application.Apps;
using Sangam.Identity.Application.Passkeys;
using Sangam.Identity.Domain;
using Sangam.Identity.Domain.Enums;
using Sangam.Identity.Server.Authentication;

namespace Sangam.Identity.Server.Pages.Account;

/// <summary>
/// Screen 1 (rc.11, A1): who is signing in — an email, a mobile number or a login ID — or a tap on a name from this
/// device's list. Nothing is looked up here, so the next screen is the same whether an account exists or not.
/// </summary>
[Antibot]
public sealed class LoginModel : AuthPageModel
{
    private readonly IAppDirectory _apps;
    private readonly IPasskeyService _passkeys;

    /// <summary>Initialises the page.</summary>
    /// <param name="apps">App directory (partner chip and sign-in policy).</param>
    /// <param name="passkeys">Passkeys (PR-14).</param>
    public LoginModel(IAppDirectory apps, IPasskeyService passkeys)
    {
        _apps = apps ?? throw new ArgumentNullException(nameof(apps));
        _passkeys = passkeys ?? throw new ArgumentNullException(nameof(passkeys));
    }

    /// <summary>An email address, a mobile number or a login ID.</summary>
    [BindProperty]
    [Required(ErrorMessage = "Enter your email, mobile number or login ID.")]
    [StringLength(SignInIdentifiers.MaxLength, ErrorMessage = "Enter your email, mobile number or login ID.")]
    public string Identifier { get; set; } = string.Empty;

    /// <summary>Whether to keep the person on this device's list once signed in.</summary>
    [BindProperty]
    public bool Remember { get; set; }

    /// <summary>Where to go after sign-in (local URLs only).</summary>
    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    /// <summary>Page-level error.</summary>
    public string? Error { get; private set; }

    /// <summary>Page-level notice (after sign-out or a password reset).</summary>
    public string? Notice { get; private set; }

    /// <summary>Whether to offer passkey sign-in (PR-14).</summary>
    public bool PasskeysEnabled => _passkeys.Enabled;

    /// <summary>Whether the application in the flow accepts only passkeys (PR-16): nothing is typed.</summary>
    public bool PasskeyOnly => (Partner?.SignInPolicy == SignInPolicy.PasskeyOnly || RequiredLevel(ReturnUrl) >= 3) && _passkeys.Enabled;

    /// <summary>The people on this device's list (rc.11).</summary>
    public IReadOnlyList<DeviceAccount> DevicePeople { get; private set; } = [];

    /// <summary>The link that picks a person from this device's list.</summary>
    /// <param name="key">The entry's key.</param>
    public string PickUrl(string key)
        => "/login/method?pick=" + Uri.EscapeDataString(key) + (string.IsNullOrEmpty(ReturnUrl) ? string.Empty : "&returnUrl=" + Uri.EscapeDataString(ReturnUrl));

    /// <summary>Renders the form.</summary>
    /// <param name="signedout">Set after sign-out.</param>
    /// <param name="reset">Set after a password reset.</param>
    /// <param name="switch">Set from the consent screen's "Switch account": ends the current session first.</param>
    /// <param name="change">Set from "Not you?": forgets what was typed.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<IActionResult> OnGetAsync(bool signedout = false, bool reset = false, bool @switch = false, bool change = false, CancellationToken cancellationToken = default)
    {
        if (@switch)
        {
            await HttpContext.SignOutAsync(IdentityConstants.ApplicationScheme);
        }
        else if (User.Identity?.IsAuthenticated == true)
        {
            return LocalRedirect(SafeReturnUrl(ReturnUrl));
        }

        if (change || @switch)
        {
            SignInFlow.Clear(HttpContext);
        }
        else if (SignInFlow.Read(HttpContext) is SignInStart start && start.PickedUserId is null)
        {
            Identifier = start.Identifier;
            Remember = start.Remember;
        }

        await ResolvePartnerAsync(_apps, ReturnUrl, cancellationToken);
        DevicePeople = DeviceAccounts.List(HttpContext);

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

    /// <summary>Remembers what was typed and moves on to how to sign in. Nothing is looked up yet.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        await ResolvePartnerAsync(_apps, ReturnUrl, cancellationToken);
        DevicePeople = DeviceAccounts.List(HttpContext);
        if (!ModelState.IsValid)
        {
            return Page();
        }

        if (SignInIdentifiers.Classify(Identifier) == SignInIdentifierKind.Invalid)
        {
            ModelState.AddModelError(nameof(Identifier), L["Enter your email, mobile number or login ID."]);
            return Page();
        }

        SignInFlow.Store(HttpContext, new SignInStart(Identifier.Trim(), Remember, Request.Form[Antibot.TimestampField].ToString()));
        return RedirectToPage("/Account/LoginMethod", new { returnUrl = ReturnUrl });
    }
}
