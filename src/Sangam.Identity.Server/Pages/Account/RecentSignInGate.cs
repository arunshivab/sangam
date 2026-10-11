using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Sangam.Web.Shared.Security;

namespace Sangam.Identity.Server.Pages.Account;

/// <summary>
/// rc.9 (ASVS V3.7.1): on the identity server's own account pages, a sensitive change (sign-in preference, passkeys) needs a
/// sign-in from the last 15 minutes. "Sign in again" ends only this browser's sign-in cookie — not the person's other sessions
/// or applications — and returns to the page once the person has signed in.
/// </summary>
public static class RecentSignInGate
{
    /// <summary>Whether the page's person signed in recently enough to change something sensitive.</summary>
    /// <param name="page">The page.</param>
    /// <returns><see langword="true"/> when recent.</returns>
    public static bool IsRecent(PageModel page)
    {
        ArgumentNullException.ThrowIfNull(page);
        // The clock is the registered TimeProvider when there is one (tests move it forward), else the system's.
        DateTimeOffset now = page.HttpContext.RequestServices.GetService<TimeProvider>()?.GetUtcNow() ?? DateTimeOffset.UtcNow;
        return RecentSignIn.IsRecent(page.User, now);
    }

    /// <summary>Signs out of this browser's sign-in and goes to the sign-in page, coming back to <paramref name="returnUrl"/>.</summary>
    /// <param name="page">The page.</param>
    /// <param name="returnUrl">A path on the identity server.</param>
    /// <returns>The redirect.</returns>
    public static async Task<IActionResult> SignInAgainAsync(PageModel page, string returnUrl)
    {
        ArgumentNullException.ThrowIfNull(page);
        await page.HttpContext.SignOutAsync(IdentityConstants.ApplicationScheme).ConfigureAwait(false);
        return new RedirectResult("/login?returnUrl=" + Uri.EscapeDataString(RecentSignIn.LocalReturn(returnUrl, "/account")));
    }
}
