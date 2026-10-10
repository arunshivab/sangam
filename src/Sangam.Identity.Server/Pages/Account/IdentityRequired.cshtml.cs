using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Sangam.Identity.Application.Accounts;
using Sangam.Identity.Application.Apps;
using Sangam.Identity.Infrastructure.Verification;
using Sangam.Identity.Server.Authentication;
using Sangam.Identity.Server.Verification;

namespace Sangam.Identity.Server.Pages.Account;

/// <summary>
/// rc.6 (SGM-914 section 4): shown when an application requires DigiLocker verification and the signed-in person is not
/// verified. They agree that their name, date of birth and gender will take the record's values, verify with DigiLocker,
/// and continue to the application.
/// </summary>
[Authorize]
public sealed class IdentityRequiredModel : AuthPageModel
{
    private readonly IAppDirectory _apps;
    private readonly IAccountService _accounts;
    private readonly DigiLockerClient _digiLocker;

    /// <summary>Initialises the page.</summary>
    /// <param name="apps">App directory.</param>
    /// <param name="accounts">Accounts.</param>
    /// <param name="digiLocker">DigiLocker.</param>
    public IdentityRequiredModel(IAppDirectory apps, IAccountService accounts, DigiLockerClient digiLocker)
    {
        _apps = apps ?? throw new ArgumentNullException(nameof(apps));
        _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
        _digiLocker = digiLocker ?? throw new ArgumentNullException(nameof(digiLocker));
    }

    /// <summary>The authorization request to resume.</summary>
    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    /// <summary>What happened on the last attempt, if anything.</summary>
    [BindProperty(SupportsGet = true)]
    public string? Result { get; set; }

    /// <summary>Whether DigiLocker is switched on.</summary>
    public bool Available => _digiLocker.Enabled;

    /// <summary>The banner for the last attempt.</summary>
    public string? Problem { get; private set; }

    /// <summary>Explains, or continues when the person is already verified.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        UserSummary? user = SangamAuthentication.UserId(User) is Guid id ? await _accounts.FindByIdAsync(id, cancellationToken) : null;
        if (user?.IdentityVerifiedAt is not null)
        {
            return LocalRedirect(SafeReturnUrl(ReturnUrl));
        }

        await ResolvePartnerAsync(_apps, ReturnUrl, cancellationToken);
        Problem = Result switch
        {
            "taken" => L["That DigiLocker account already verifies another Sangam account. Sign in with that account instead."].Value,
            "denied" => L["DigiLocker was not allowed to share your details. Try again when you are ready."].Value,
            "failed" => L["DigiLocker did not answer. Please try again."].Value,
            "expired" => L["That took too long. Please start again."].Value,
            _ => null,
        };
        return Page();
    }

    /// <summary>Sends the person to DigiLocker.</summary>
    public IActionResult OnPost()
    {
        if (SangamAuthentication.UserId(User) is not Guid userId || !Available)
        {
            return RedirectToPage();
        }

        if (Request.Form["agree"] != "true")
        {
            return RedirectToPage(new { returnUrl = ReturnUrl });
        }

        return Redirect(IdentityDigiLocker.Start(HttpContext, IdentityDigiLocker.Verify, userId, SafeReturnUrl(ReturnUrl)));
    }
}
