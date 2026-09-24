using Microsoft.AspNetCore.Mvc.RazorPages;
using Sangam.Identity.Application.Apps;
using Sangam.Identity.Server.Authorization;

namespace Sangam.Identity.Server.Pages.Account;

/// <summary>Shared helpers for the auth screens.</summary>
public abstract class AuthPageModel : PageModel
{
    /// <summary>The partner app in the flow, when the screen was reached from the authorization endpoint.</summary>
    public AppSummary? Partner { get; private set; }

    /// <summary>Client IP as seen by Kestrel (forwarded headers are configured in PR-08).</summary>
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
}
