using Microsoft.AspNetCore.Mvc;
using Sangam.Identity.Application.Accounts;

namespace Sangam.Identity.Server.Pages.Account;

/// <summary>
/// rc.7: the one-click "this wasn't me, cancel" link from the e-mails about a merge. No sign-in is needed: the link is the
/// proof. Opening it shows one button, so a mail scanner that follows links cannot cancel a merge the person wanted.
/// </summary>
public sealed class MergeCancelModel : AuthPageModel
{
    private readonly IAccountMergeService _merges;

    /// <summary>Initialises the page.</summary>
    /// <param name="merges">Account merges.</param>
    public MergeCancelModel(IAccountMergeService merges)
    {
        _merges = merges ?? throw new ArgumentNullException(nameof(merges));
    }

    /// <summary>The token from the link.</summary>
    [BindProperty(SupportsGet = true)]
    public string Token { get; set; } = string.Empty;

    /// <summary>The pending merge behind the token, if it is still pending.</summary>
    public PendingMerge? Pending { get; private set; }

    /// <summary>Whether it was cancelled now.</summary>
    public bool Done { get; private set; }

    /// <summary>Shows the button.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Pending = await _merges.FindByTokenAsync(Token, cancellationToken);
    }

    /// <summary>Cancels the merge.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        Done = await _merges.CancelByTokenAsync(Token, ClientIp, cancellationToken);
        if (!Done)
        {
            Pending = await _merges.FindByTokenAsync(Token, cancellationToken);
        }

        return Page();
    }
}
