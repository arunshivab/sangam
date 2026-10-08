using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Sangam.Identity.Application.Accounts;

namespace Sangam.Identity.Server.Pages.Account;

/// <summary>
/// The one-click "this wasn't me, cancel" link from the e-mail about a support reset of two-step sign-in (D-K). No
/// sign-in is needed: the link is the proof. Opening it shows one button, so a mail scanner that follows links
/// cannot cancel a reset the person wanted.
/// </summary>
public sealed class ResetCancelModel : AuthPageModel
{
    private static readonly TimeSpan India = TimeSpan.FromHours(5.5);
    private readonly IMfaResetService _resets;

    /// <summary>Initialises the page.</summary>
    /// <param name="resets">Two-step resets.</param>
    public ResetCancelModel(IMfaResetService resets)
    {
        _resets = resets ?? throw new ArgumentNullException(nameof(resets));
    }

    /// <summary>The token from the link.</summary>
    [BindProperty(SupportsGet = true)]
    public string Token { get; set; } = string.Empty;

    /// <summary>The pending reset behind the token, if it is still pending.</summary>
    public PendingTwoStepReset? Pending { get; private set; }

    /// <summary>Whether it was cancelled now.</summary>
    public bool Done { get; private set; }

    /// <summary>When it takes effect.</summary>
    public string EffectiveText => Pending is null ? string.Empty : When(Pending.EffectiveAt);

    /// <summary>A time in India, in the reader's language.</summary>
    /// <param name="at">The time.</param>
    public static string When(DateTimeOffset at)
        => at.ToOffset(India).ToString("f", CultureInfo.CurrentCulture) + " IST";

    /// <summary>Shows the button.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Pending = await _resets.FindByTokenAsync(Token, cancellationToken);
    }

    /// <summary>Cancels the reset.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        Done = await _resets.CancelByTokenAsync(Token, ClientIp, cancellationToken);
        if (!Done)
        {
            Pending = await _resets.FindByTokenAsync(Token, cancellationToken);
        }

        return Page();
    }
}
