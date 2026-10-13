using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Sangam.Identity.Application.Apps;
using Sangam.Identity.Application.Passkeys;
using Sangam.Identity.Application.Security;
using Sangam.Identity.Domain.Enums;
using Sangam.Identity.Server.Authentication;

namespace Sangam.Identity.Server.Pages.Account;

/// <summary>
/// Shown when an application's or organisation's policy requires a second factor and the person has none yet
/// (PR-16). They set up an authenticator in the portal or add a passkey here, then sign in again.
/// </summary>
[Authorize]
public sealed class TwoStepRequiredModel : AuthPageModel
{
    private readonly IAppDirectory _apps;
    private readonly IPasskeyService _passkeys;
    private readonly IConfiguration _configuration;
    private readonly ISecurityPolicyService _policies;

    /// <summary>Initialises the page.</summary>
    /// <param name="apps">App directory.</param>
    /// <param name="passkeys">Passkeys.</param>
    /// <param name="configuration">Configuration, for the portal's address.</param>
    /// <param name="policies">Security policies, to say why two-step sign-in is asked (rc.11).</param>
    public TwoStepRequiredModel(IAppDirectory apps, IPasskeyService passkeys, IConfiguration configuration, ISecurityPolicyService policies)
    {
        _apps = apps ?? throw new ArgumentNullException(nameof(apps));
        _passkeys = passkeys ?? throw new ArgumentNullException(nameof(passkeys));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _policies = policies ?? throw new ArgumentNullException(nameof(policies));
    }

    /// <summary>The authorization request to resume.</summary>
    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    /// <summary>Whether passkeys are offered.</summary>
    public bool OfferPasskeys => _passkeys.Enabled;

    /// <summary>Where the authenticator is set up (rc.10 moved it to Sign-in &amp; security).</summary>
    public string AuthenticatorUrl => (_configuration["Sangam:PortalUrl"] ?? "https://account.sangamid.in").TrimEnd('/') + "/security";

    /// <summary>Whether the rule asks only the people who administer the application (rc.11).</summary>
    public bool ForAdministrators { get; private set; }

    /// <summary>Explains what is needed.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        await ResolvePartnerAsync(_apps, ReturnUrl, cancellationToken);
        if (SangamAuthentication.UserId(User) is Guid userId)
        {
            PersonPolicy person = await _policies.ForPersonAsync(userId, Partner?.Id, cancellationToken);
            ForAdministrators = person.Policy.Mfa == MfaRequirement.RequiredForAdministrators && person.IsAdministrator;
        }

        return Page();
    }

    /// <summary>Signs out of this session and starts a fresh sign-in, which will now include the second factor.</summary>
    public async Task<IActionResult> OnPostAsync()
    {
        await HttpContext.SignOutAsync(IdentityConstants.ApplicationScheme);
        return Redirect(WithReturn("/login", ReturnUrl is not null && Url.IsLocalUrl(ReturnUrl) ? ReturnUrl : null));
    }
}
