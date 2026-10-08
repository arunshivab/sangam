using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Sangam.Identity.Application.Accounts;
using Sangam.Identity.Application.Apps;
using Sangam.Identity.Domain.Enums;
using Sangam.Identity.Server.Authentication;
using Sangam.Identity.Server.Authorization;

namespace Sangam.Identity.Server.Pages.Account;

/// <summary>Shared helpers for the auth screens.</summary>
public abstract class AuthPageModel : PageModel
{
    /// <summary>The partner app in the flow, when the screen was reached from the authorization endpoint.</summary>
    public AppSummary? Partner { get; private set; }

    /// <summary>Client IP as seen by Kestrel (forwarded headers are configured for production in PR-10).</summary>
    protected string? ClientIp => HttpContext.Connection.RemoteIpAddress?.ToString();

    /// <summary>Client user agent, truncated to a sane length for the audit log.</summary>
    protected string? ClientUserAgent
    {
        get
        {
            string ua = Request.Headers.UserAgent.ToString();
            return string.IsNullOrEmpty(ua) ? null : (ua.Length > 500 ? ua[..500] : ua);
        }
    }

    /// <summary>Only ever redirect to a path on this host.</summary>
    /// <param name="returnUrl">Candidate.</param>
    /// <returns>The candidate when local, else <c>/account</c>.</returns>
    protected string SafeReturnUrl(string? returnUrl) => !string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl) ? returnUrl : "/account";

    /// <summary>Resolves the partner chip for <paramref name="returnUrl"/> and exposes it to the layout.</summary>
    /// <param name="apps">App directory.</param>
    /// <param name="returnUrl">Local return URL.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    protected async Task ResolvePartnerAsync(IAppDirectory apps, string? returnUrl, CancellationToken cancellationToken)
    {
        Partner = Url.IsLocalUrl(returnUrl) ? await PartnerContext.ResolveAsync(apps, returnUrl, cancellationToken) : null;
        ViewData["Partner"] = Partner;
    }

    /// <summary>Appends the return URL to a local link when there is one.</summary>
    /// <param name="path">Local path.</param>
    /// <param name="returnUrl">Return URL.</param>
    protected static string WithReturn(string path, string? returnUrl)
        => string.IsNullOrEmpty(returnUrl) ? path : path + "?returnUrl=" + Uri.EscapeDataString(returnUrl);

    /// <summary>
    /// Continues a sign-in whose password was accepted and meets every policy: the code step, then the
    /// authenticator step, then the session — whichever apply (PR-16 shares this between the sign-in page and the
    /// new-password step).
    /// </summary>
    /// <param name="accounts">Accounts, to record the sign-in.</param>
    /// <param name="user">The person (fresh, so the security stamp is current).</param>
    /// <param name="mode">The mode the sign-in must complete.</param>
    /// <param name="returnUrl">Where to go after.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    protected async Task<IActionResult> ContinueAfterPasswordAsync(IAccountService accounts, UserSummary user, SignInMode mode, string? returnUrl, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(accounts);
        ArgumentNullException.ThrowIfNull(user);
        if (mode == SignInMode.PasswordAndOtp)
        {
            await SangamAuthentication.StorePendingAsync(HttpContext, SangamAuthentication.Pending.SignInOtp, user.Id, mode);
            return RedirectToPage("/Account/LoginVerify", new { returnUrl });
        }

        if (user.MfaEnrolled)
        {
            // The password was right, but an authenticator is enrolled: park it and ask.
            await SangamAuthentication.StorePendingAsync(HttpContext, SangamAuthentication.Pending.Authenticator, user.Id, mode);
            return RedirectToPage("/Account/LoginMfa", new { returnUrl });
        }

        await SangamAuthentication.SignInSessionAsync(HttpContext, user, mode, Partner?.Id, PartnerContext.DeviceLabelFromReturnUrl(returnUrl));
        await accounts.RecordSignInAsync(user.Id, mode, ClientIp, ClientUserAgent, cancellationToken);
        return LocalRedirect(SafeReturnUrl(returnUrl));
    }
}
