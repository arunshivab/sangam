using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Sangam.Identity.Application.Accounts;
using Sangam.Identity.Server.Authentication;

namespace Sangam.Identity.Server.Pages.Account;

/// <summary>
/// The notice at sign-in (D-K): a support reset of the person's two-step sign-in is waiting out its cooling-off
/// period. One click cancels it ("this wasn't me"); otherwise they continue where they were going.
/// </summary>
[Authorize]
public sealed class ResetNoticeModel : AuthPageModel
{
    private readonly IMfaResetService _resets;

    /// <summary>Initialises the page.</summary>
    /// <param name="resets">Two-step resets.</param>
    public ResetNoticeModel(IMfaResetService resets)
    {
        _resets = resets ?? throw new ArgumentNullException(nameof(resets));
    }

    /// <summary>Where to go after.</summary>
    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    /// <summary>The pending reset, if any.</summary>
    public PendingTwoStepReset? Pending { get; private set; }

    /// <summary>Whether it was just cancelled.</summary>
    public bool Cancelled { get; private set; }

    /// <summary>When it takes effect, in the reader's language and India time.</summary>
    public string EffectiveText => Pending is null ? string.Empty : ResetCancelModel.When(Pending.EffectiveAt);

    /// <summary>Where Continue goes.</summary>
    public string ContinueUrl => SafeReturnUrl(ReturnUrl);

    /// <summary>Shows the notice.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Pending = SangamAuthentication.UserId(User) is Guid userId ? await _resets.PendingAsync(userId, cancellationToken) : null;
    }

    /// <summary>"This wasn't me": cancels the reset.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<IActionResult> OnPostCancelAsync(CancellationToken cancellationToken)
    {
        if (SangamAuthentication.UserId(User) is not Guid userId)
        {
            return Challenge();
        }

        Cancelled = await _resets.CancelByOwnerAsync(userId, ClientIp, cancellationToken);
        return Page();
    }

    /// <summary>"It was me": continues, and does not show the notice again.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<IActionResult> OnPostContinueAsync(CancellationToken cancellationToken)
    {
        if (SangamAuthentication.UserId(User) is Guid userId)
        {
            await _resets.AcknowledgeNoticeAsync(userId, cancellationToken);
        }

        return LocalRedirect(SafeReturnUrl(ReturnUrl));
    }
}
