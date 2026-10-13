using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Sangam.Identity.Server.Pages.Account;

/// <summary>
/// rc.10 (design review, 12 Oct 2026): the identity server's own "Your account" page is retired — it repeated the account
/// portal. <c>/account</c> now sends a signed-in person to the portal (Sign-in &amp; security after a password change). The
/// task pages that must stay here, because only the identity server can do them, remain: <c>/account/password</c>,
/// <c>/account/passkeys</c>, <c>/account/mobile</c> and <c>/account/merge</c>.
/// </summary>
[Authorize]
public sealed class HomeModel : PageModel
{
    private readonly IConfiguration _configuration;

    /// <summary>Initialises the page.</summary>
    /// <param name="configuration">Configuration, for the portal's address.</param>
    public HomeModel(IConfiguration configuration)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
    }

    /// <summary>Sends the person to the account portal.</summary>
    /// <param name="passwordChanged">The password was just changed (from <c>/account/password</c>).</param>
    /// <returns>The redirect.</returns>
    public IActionResult OnGet(bool passwordChanged)
    {
        string portal = (_configuration["Sangam:PortalUrl"] ?? "https://account.sangamid.in").TrimEnd('/');
        return Redirect(passwordChanged ? portal + "/security?notice=password-changed" : portal + "/");
    }
}
