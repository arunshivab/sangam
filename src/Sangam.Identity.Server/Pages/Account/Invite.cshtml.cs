using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Sangam.Identity.Application.Partners;
using Sangam.Identity.Server.Authentication;

namespace Sangam.Identity.Server.Pages.Account;

/// <summary>
/// Accepting an invitation (PR-13). Signing in is required, and the account's verified address must
/// be the invited one; only then is the application linked and the role given (D-093).
/// </summary>
[Authorize]
public sealed class InviteModel : PageModel
{
    private readonly IInvitationService _invitations;

    /// <summary>Initialises the page.</summary>
    /// <param name="invitations">Invitations.</param>
    public InviteModel(IInvitationService invitations)
    {
        _invitations = invitations ?? throw new ArgumentNullException(nameof(invitations));
    }

    /// <summary>The token from the link.</summary>
    [BindProperty(SupportsGet = true)]
    public string Token { get; set; } = string.Empty;

    /// <summary>The invitation, or <see langword="null"/> when the token is unknown.</summary>
    public InvitationView? View { get; private set; }

    /// <summary>The outcome of accepting.</summary>
    public string? Message { get; private set; }

    /// <summary>Whether the invitation was accepted now.</summary>
    public bool Succeeded { get; private set; }

    /// <summary>The text catalogue, in the request's language (PR-18).</summary>
    private Microsoft.Extensions.Localization.IStringLocalizer L => PageText.For(HttpContext);

    /// <summary>Why the invitation cannot be accepted.</summary>
    public string StateText => View?.State switch
    {
        InvitationState.Used => L["This invitation has already been accepted."],
        InvitationState.Expired => L["This invitation has expired. Ask for a new one."],
        _ => L["This invitation is no longer valid."],
    };

    /// <summary>Shows the invitation.</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        View = await _invitations.GetAsync(Token, cancellationToken);
    }

    /// <summary>Accepts the invitation for the signed-in person.</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        Guid? userId = SangamAuthentication.UserId(User);
        if (userId is null)
        {
            return Challenge();
        }

        PartnerResult result = await _invitations.AcceptAsync(Token, userId.Value, cancellationToken);
        Succeeded = result.Succeeded;
        Message = L[result.Message ?? string.Empty];
        View = await _invitations.GetAsync(Token, cancellationToken);
        return Page();
    }
}
