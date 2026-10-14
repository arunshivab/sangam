using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Sangam.Identity.Application.Tenancy;
using Sangam.Identity.Server.Authentication;
using Sangam.Identity.Server.Staff;

namespace Sangam.Identity.Server.Pages.Account;

/// <summary>
/// rc.12 (4.2 and 4.4 of the design): the link an admin's addition e-mails the person. Nothing happens without the code the
/// admin gave them in person; after it, the person joins with the account the admin found, or creates one with the address
/// the link went to.
/// </summary>
public sealed class StaffModel : AuthPageModel
{
    private readonly IStaffService _staff;
    private readonly IDataProtectionProvider _protection;

    /// <summary>Initialises the page.</summary>
    /// <param name="staff">Staff service.</param>
    /// <param name="protection">Data protection, for the activation ticket.</param>
    public StaffModel(IStaffService staff, IDataProtectionProvider protection)
    {
        _staff = staff ?? throw new ArgumentNullException(nameof(staff));
        _protection = protection ?? throw new ArgumentNullException(nameof(protection));
    }

    /// <summary>The token from the link.</summary>
    [BindProperty(SupportsGet = true)]
    public string Token { get; set; } = string.Empty;

    /// <summary>The ticket a right code gave, carried through signing in or registering.</summary>
    [BindProperty(SupportsGet = true)]
    public string? Ticket { get; set; }

    /// <summary>The activation code typed.</summary>
    [BindProperty]
    public string Code { get; set; } = string.Empty;

    /// <summary>The invite, or <see langword="null"/> for an unknown link.</summary>
    public StaffInviteView? View { get; private set; }

    /// <summary>Whether the right code has been entered (a valid ticket for this link).</summary>
    public bool CodeAccepted { get; private set; }

    /// <summary>Whether someone is signed in.</summary>
    public bool SignedIn { get; private set; }

    /// <summary>A message after an action.</summary>
    public string? Message { get; private set; }

    /// <summary>The message's tone: <c>success</c> or <c>danger</c>.</summary>
    public string Tone { get; private set; } = "danger";

    /// <summary>Whether the person has joined now.</summary>
    public bool Joined { get; private set; }

    /// <summary>Where signing in or registering comes back to.</summary>
    public string BackHere => $"/staff/{Token}?ticket={Uri.EscapeDataString(Ticket ?? string.Empty)}";

    /// <summary>Shows the link.</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        await LoadAsync(cancellationToken);
    }

    /// <summary>Checks the activation code.</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    public async Task<IActionResult> OnPostCodeAsync(CancellationToken cancellationToken)
    {
        StaffCodeResult result = await _staff.CheckCodeAsync(Token, Code, cancellationToken);
        if (result.Status == StaffCodeStatus.Right && result.InviteId is Guid inviteId)
        {
            return Redirect($"/staff/{Token}?ticket={Uri.EscapeDataString(StaffTicket.Issue(_protection, inviteId))}");
        }

        Ticket = null;
        await LoadAsync(cancellationToken);
        Message = result.Status switch
        {
            StaffCodeStatus.Wrong => L["That code is not right. You have {0} more tries.", result.AttemptsLeft],
            StaffCodeStatus.Locked => L["That code was wrong five times, so this link no longer works. Ask your admin to add you again."],
            _ => L["This link no longer works."],
        };
        return Page();
    }

    /// <summary>Joins, for the signed-in person.</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    public async Task<IActionResult> OnPostJoinAsync(CancellationToken cancellationToken)
    {
        Guid? inviteId = StaffTicket.Read(_protection, Ticket);
        Guid? userId = SangamAuthentication.UserId(User);
        if (inviteId is null || userId is null)
        {
            await LoadAsync(cancellationToken);
            return Page();
        }

        StaffJoinResult result = await _staff.JoinAsync(inviteId.Value, userId.Value, cancellationToken);
        await LoadAsync(cancellationToken);
        Joined = result.Status == StaffJoinStatus.Joined;
        Tone = Joined ? "success" : "danger";
        Message = L[result.Message];
        return Page();
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        View = await _staff.GetInviteAsync(Token, cancellationToken);
        SignedIn = SangamAuthentication.UserId(User) is not null;
        CodeAccepted = View is { State: StaffInviteState.Open } && StaffTicket.Read(_protection, Ticket) is not null;
    }
}
