using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Sangam.Identity.Application.Accounts;
using Sangam.Identity.Application.Apps;
using Sangam.Identity.Application.Passkeys;
using Sangam.Identity.Domain.Enums;
using Sangam.Identity.Server.Authentication;
using Sangam.Identity.Server.Authorization;

namespace Sangam.Identity.Server.Pages.Account;

/// <summary>
/// Passkey sign-in (PR-14). Two JSON steps called by passkeys.js from the sign-in page: options,
/// then verify. On success the session is created exactly as a password sign-in creates it, with
/// mode "passkey", which satisfies every application's sign-in rule (SGM-205 §4.2).
/// </summary>
public sealed class LoginPasskeyModel : AuthPageModel
{
    private readonly IPasskeyService _passkeys;
    private readonly IAccountService _accounts;
    private readonly IAppDirectory _apps;

    /// <summary>Initialises the page.</summary>
    /// <param name="passkeys">Passkeys.</param>
    /// <param name="accounts">Accounts.</param>
    /// <param name="apps">Application directory.</param>
    public LoginPasskeyModel(IPasskeyService passkeys, IAccountService accounts, IAppDirectory apps)
    {
        _passkeys = passkeys ?? throw new ArgumentNullException(nameof(passkeys));
        _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
        _apps = apps ?? throw new ArgumentNullException(nameof(apps));
    }

    /// <summary>Where to go after signing in.</summary>
    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    /// <summary>This page has nothing to show.</summary>
    public IActionResult OnGet() => RedirectToPage("/Account/Login", new { returnUrl = ReturnUrl });

    /// <summary>Starts a sign-in ceremony.</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    public async Task<IActionResult> OnPostOptionsAsync(CancellationToken cancellationToken)
    {
        if (!_passkeys.Enabled)
        {
            return NotFound();
        }

        PasskeyCeremony ceremony = await _passkeys.BeginSignInAsync(cancellationToken);
        return Content($"{{\"challengeId\":\"{ceremony.ChallengeId:D}\",\"options\":{ceremony.OptionsJson}}}", "application/json");
    }

    /// <summary>Verifies the browser's answer and signs the person in.</summary>
    /// <param name="answer">The answer.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public async Task<IActionResult> OnPostVerifyAsync([FromBody] PasskeyAnswer answer, CancellationToken cancellationToken)
    {
        if (answer is null)
        {
            // A body that does not bind is a broken or forged request, not a server fault.
            return new BadRequestObjectResult(new { error = "That passkey answer could not be read. Please try again." });
        }

        if (!_passkeys.Enabled)
        {
            return NotFound();
        }

        PasskeyResult result = await _passkeys.CompleteSignInAsync(answer.ChallengeId, answer.Credential, cancellationToken);
        UserSummary? user = result.Succeeded && result.UserId is Guid id ? await _accounts.FindByIdAsync(id, cancellationToken) : null;
        if (user is null)
        {
            return new JsonResult(new { error = result.Message });
        }

        await ResolvePartnerAsync(_apps, ReturnUrl, cancellationToken);
        await SangamAuthentication.SignInSessionAsync(HttpContext, user, SignInMode.Passkey, Partner?.Id, PartnerContext.DeviceLabelFromReturnUrl(ReturnUrl));
        await _accounts.RecordSignInAsync(user.Id, SignInMode.Passkey, ClientIp, ClientUserAgent, cancellationToken);
        return new JsonResult(new { redirect = SafeReturnUrl(ReturnUrl) }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }
}
