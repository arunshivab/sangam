using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Sangam.Identity.Application.Abstractions;
using Sangam.Identity.Application.Apps;
using Sangam.Identity.Application.Passkeys;
using Sangam.Identity.Domain.Enums;
using Sangam.Identity.Server.Authentication;

namespace Sangam.Identity.Server.Pages.Account;

/// <summary>
/// Screen 2 (rc.11, A2): how to sign in. The choices depend only on the application's rule — never on the account — so
/// the screen is the same for every email, mobile number or login ID, whether an account exists or not. Strongest first:
/// a passkey, the password, a code by email (for accounts that chose a code instead of a password).
/// </summary>
public sealed class LoginMethodModel : AuthPageModel
{
    private readonly IAppDirectory _apps;
    private readonly IPasskeyService _passkeys;
    private readonly IDataProtectionProvider _protection;
    private readonly IClock _clock;

    /// <summary>Initialises the page.</summary>
    /// <param name="apps">App directory (the application's rule).</param>
    /// <param name="passkeys">Passkeys.</param>
    /// <param name="protection">Data protection, for the bot-check token of a name picked from this device's list.</param>
    /// <param name="clock">Clock.</param>
    public LoginMethodModel(IAppDirectory apps, IPasskeyService passkeys, IDataProtectionProvider protection, IClock clock)
    {
        _apps = apps ?? throw new ArgumentNullException(nameof(apps));
        _passkeys = passkeys ?? throw new ArgumentNullException(nameof(passkeys));
        _protection = protection ?? throw new ArgumentNullException(nameof(protection));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <summary>Where to go after sign-in.</summary>
    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    /// <summary>What the first screen remembered.</summary>
    public SignInStart Start { get; private set; } = new(string.Empty, false, null);

    /// <summary>The bot-check token the code button posts with.</summary>
    public string FormToken { get; private set; } = string.Empty;

    /// <summary>Whether to offer a passkey.</summary>
    public bool ShowPasskey { get; private set; }

    /// <summary>Whether to offer the password.</summary>
    public bool ShowPassword { get; private set; }

    /// <summary>Whether to offer a code by email.</summary>
    public bool ShowCode { get; private set; }

    /// <summary>The password screen's link.</summary>
    public string PasswordUrl => WithReturn("/login/password", ReturnUrl);

    /// <summary>Shows the choices.</summary>
    /// <param name="pick">The key of a name picked from this device's list.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<IActionResult> OnGetAsync(string? pick = null, CancellationToken cancellationToken = default)
    {
        if (User.Identity?.IsAuthenticated == true)
        {
            return LocalRedirect(SafeReturnUrl(ReturnUrl));
        }

        if (!string.IsNullOrEmpty(pick))
        {
            DeviceAccount? person = DeviceAccounts.List(HttpContext).FirstOrDefault(a => string.Equals(a.Key, pick, StringComparison.Ordinal));
            if (person is null)
            {
                return Redirect(WithReturn("/login", ReturnUrl));
            }

            // A name on this device's list: only browsers holding the protected list can pick it, so the bot check's clock
            // starts as though the first screen had been shown a moment ago.
            Start = new SignInStart(person.FirstName + " · " + person.MaskedEmail, true, Antibot.CreateToken(_protection, _clock.UtcNow.AddMinutes(-1)), person.UserId);
            SignInFlow.Store(HttpContext, Start);
        }
        else if (SignInFlow.Read(HttpContext) is SignInStart start)
        {
            Start = start;
        }
        else
        {
            return Redirect(WithReturn("/login", ReturnUrl));
        }

        await ResolvePartnerAsync(_apps, ReturnUrl, cancellationToken);
        ExposeReturnQuery(ReturnUrl);
        FormToken = string.IsNullOrEmpty(Start.FormToken) ? Antibot.CreateToken(_protection, _clock.UtcNow) : Start.FormToken;

        SignInPolicy? policy = Partner?.SignInPolicy;
        int level = RequiredLevel(ReturnUrl);
        bool passkeyOnly = policy == SignInPolicy.PasskeyOnly || level >= 3;
        ShowPasskey = _passkeys.Enabled;
        ShowPassword = !passkeyOnly && policy != SignInPolicy.OtpOnly;
        // A code alone is one step: not offered when the application asks for two steps or more.
        ShowCode = !passkeyOnly && level < 2 && policy is null or SignInPolicy.Default or SignInPolicy.OtpOnly;
        if (!ShowPasskey && !ShowPassword && !ShowCode)
        {
            ShowPassword = true;
        }

        return Page();
    }
}
