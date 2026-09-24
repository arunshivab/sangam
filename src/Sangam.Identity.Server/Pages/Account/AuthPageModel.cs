using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Sangam.Identity.Server.Pages.Account;

/// <summary>Shared helpers for the auth screens.</summary>
public abstract class AuthPageModel : PageModel
{
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
}
