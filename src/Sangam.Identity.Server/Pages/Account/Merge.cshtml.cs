using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Sangam.Identity.Application.Accounts;
using Sangam.Identity.Application.Portal;
using Sangam.Identity.Server.Authentication;

namespace Sangam.Identity.Server.Pages.Account;

/// <summary>
/// rc.7 (SGM-914 section 9): a person merges two of their Sangam accounts. They start signed in to one, sign in fully to
/// the other (each with its own second step), then choose which account stays. Both sign-ins must be recent, so nobody
/// can merge an account from a session left open. The merge waits 24 hours (72 for administrators) with a cancel link
/// to both accounts.
/// </summary>
[Authorize]
public sealed class MergeModel : AuthPageModel
{
    /// <summary>How recent each sign-in must be.</summary>
    public static readonly TimeSpan Freshness = Sangam.Web.Shared.Security.RecentSignIn.Window;

    /// <summary>The cookie's base name; <c>__Secure-</c> over HTTPS.</summary>
    public const string CookieName = "sangam.merge";

    private const string CookiePath = "/account/merge";
    private const string Protection = "Sangam.Identity.Merge.v1";

    private readonly IAccountMergeService _merges;
    private readonly IAccountService _accounts;
    private readonly ISessionService _sessions;
    private readonly IDataProtectionProvider _protection;

    /// <summary>Initialises the page.</summary>
    /// <param name="merges">Account merges.</param>
    /// <param name="accounts">Accounts.</param>
    /// <param name="sessions">Sessions, to end this one before the second sign-in.</param>
    /// <param name="protection">Data protection, for the cookie that carries the first account.</param>
    public MergeModel(IAccountMergeService merges, IAccountService accounts, ISessionService sessions, IDataProtectionProvider protection)
    {
        _merges = merges ?? throw new ArgumentNullException(nameof(merges));
        _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _protection = protection ?? throw new ArgumentNullException(nameof(protection));
    }

    /// <summary>A merge already waiting on this account.</summary>
    public PendingMerge? Pending { get; private set; }

    /// <summary>The two accounts side by side, after the second sign-in.</summary>
    public MergePreview? Preview { get; private set; }

    /// <summary>Why this account cannot start a merge.</summary>
    public string? StartRefusal { get; private set; }

    /// <summary>Whether this sign-in is recent and complete enough to merge.</summary>
    public bool Fresh { get; private set; }

    /// <summary>Whether the person signed in to the account they started from again.</summary>
    public bool SameAccount { get; private set; }

    /// <summary>A message to show.</summary>
    [TempData]
    public string? Notice { get; set; }

    /// <summary>The account that stays: <c>first</c> or <c>second</c>.</summary>
    [BindProperty]
    public string? Survivor { get; set; }

    /// <summary>Shows the step the person is at.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        if (SangamAuthentication.UserId(User) is not Guid me)
        {
            return Challenge();
        }

        Pending = await _merges.PendingAsync(me, cancellationToken);
        if (Pending is not null)
        {
            return Page();
        }

        Fresh = await IsFreshAsync(me, cancellationToken);
        Started? started = ReadCookie();
        if (started is not null && started.First == me)
        {
            SameAccount = true;
        }
        else if (started is not null && Fresh)
        {
            Preview = await _merges.PreviewAsync(started.First, me, cancellationToken);
            return Page();
        }

        StartRefusal = await _merges.CanStartAsync(me, cancellationToken);
        return Page();
    }

    /// <summary>Remembers this account, ends this session and sends the person to sign in to the other.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<IActionResult> OnPostStartAsync(CancellationToken cancellationToken)
    {
        if (SangamAuthentication.UserId(User) is not Guid me)
        {
            return Challenge();
        }

        if (!await IsFreshAsync(me, cancellationToken) || await _merges.CanStartAsync(me, cancellationToken) is not null)
        {
            return RedirectToPage();
        }

        Started started = new(me, SangamAuthentication.AuthenticatedAt(User) ?? DateTimeOffset.UtcNow);
        Response.Cookies.Append(CookieNameFor(), _protection.CreateProtector(Protection).Protect(JsonSerializer.Serialize(started)), new CookieOptions
        {
            HttpOnly = true,
            Secure = Request.IsHttps,
            SameSite = SameSiteMode.Lax,
            Path = CookiePath,
            MaxAge = Freshness,
            IsEssential = true,
        });
        return await SignOutToAsync(cancellationToken);
    }

    /// <summary>Signs in to this account again, for a sign-in that is no longer recent.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<IActionResult> OnPostAgainAsync(CancellationToken cancellationToken) => SignOutToAsync(cancellationToken);

    /// <summary>Forgets the account the person started from.</summary>
    public IActionResult OnPostRestart()
    {
        Response.Cookies.Delete(CookieNameFor(), new CookieOptions { Path = CookiePath });
        return RedirectToPage();
    }

    /// <summary>Asks for the merge: the waiting period starts and both accounts are told.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<IActionResult> OnPostConfirmAsync(CancellationToken cancellationToken)
    {
        if (SangamAuthentication.UserId(User) is not Guid me)
        {
            return Challenge();
        }

        Started? started = ReadCookie();
        if (started is null || started.First == me || !await IsFreshAsync(me, cancellationToken) || Request.Form["agree"] != "true" || Survivor is not ("first" or "second"))
        {
            return RedirectToPage();
        }

        (Guid survivor, Guid closed) = Survivor == "first" ? (started.First, me) : (me, started.First);
        MergeOutcome outcome = await _merges.RequestAsync(survivor, closed, ClientIp, cancellationToken);
        Response.Cookies.Delete(CookieNameFor(), new CookieOptions { Path = CookiePath });
        Notice = outcome.Succeeded
            ? L["The merge is asked for. Both accounts were e-mailed a link to cancel it."].Value
            : RefusalText(outcome.Refusal);
        return RedirectToPage();
    }

    /// <summary>Cancels the merge waiting on this account.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<IActionResult> OnPostCancelAsync(CancellationToken cancellationToken)
    {
        if (SangamAuthentication.UserId(User) is Guid me && await _merges.CancelByOwnerAsync(me, ClientIp, cancellationToken))
        {
            Notice = L["The merge is cancelled. Nothing about either account has changed."].Value;
        }

        return RedirectToPage();
    }

    /// <summary>The words for a refusal code.</summary>
    /// <param name="code">The code.</param>
    public string RefusalText(string? code) => code switch
    {
        "same" => L["You signed in to the same account twice. Sign in to your other account."].Value,
        "inactive" => L["One of the accounts is suspended, on hold or being deleted, so it cannot be merged."].Value,
        "operator" => L["A Sangam operator's account cannot be merged this way. Write to help@sangamid.in."].Value,
        "different_identity" => L["The two accounts are verified with different DigiLocker identities. They belong to two different people and cannot be merged."].Value,
        "pending" => L["A merge or a recovery is already waiting on one of the accounts. Let it finish, or cancel it, first."].Value,
        _ => L["These accounts cannot be merged."].Value,
    };

    /// <summary>A time in India, in the reader's language.</summary>
    /// <param name="at">The time.</param>
    public static string When(DateTimeOffset at) => Sangam.Shared.IndiaTime.Full(at);

    /// <summary>Recent, and with the second step when the account has one.</summary>
    private async Task<bool> IsFreshAsync(Guid me, CancellationToken cancellationToken)
    {
        if (SangamAuthentication.AuthenticatedAt(User) is not DateTimeOffset at || DateTimeOffset.UtcNow - at > Freshness)
        {
            return false;
        }

        UserSummary? user = await _accounts.FindByIdAsync(me, cancellationToken);
        return user is not null && (!user.MfaEnrolled || SangamAuthentication.HasSecondFactor(User));
    }

    private async Task<IActionResult> SignOutToAsync(CancellationToken cancellationToken)
    {
        if (SangamAuthentication.SessionId(User) is Guid session)
        {
            await _sessions.EndAsync(session, "user", cancellationToken);
        }

        await HttpContext.SignOutAsync(IdentityConstants.ApplicationScheme);
        return Redirect("/login?returnUrl=" + Uri.EscapeDataString(CookiePath));
    }

    private Started? ReadCookie()
    {
        if (!Request.Cookies.TryGetValue(CookieNameFor(), out string? value) || string.IsNullOrEmpty(value))
        {
            return null;
        }

        try
        {
            Started? started = JsonSerializer.Deserialize<Started>(_protection.CreateProtector(Protection).Unprotect(value));
            return started is not null && DateTimeOffset.UtcNow - started.FirstSignedInAt <= Freshness ? started : null;
        }
        catch (Exception exception) when (exception is CryptographicException or JsonException)
        {
            return null;
        }
    }

    private string CookieNameFor() => Request.IsHttps ? "__Secure-" + CookieName : CookieName;

    /// <summary>The account the person started from, and when they signed in to it.</summary>
    private sealed record Started(Guid First, DateTimeOffset FirstSignedInAt);
}
