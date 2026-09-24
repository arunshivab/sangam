using Microsoft.AspNetCore.Authorization;

namespace Sangam.Identity.Server.Pages.Account;

/// <summary>Screen 4 — email verified. Partner "Continue to …" arrives with the app-initiated flow in PR-04.</summary>
[Authorize]
public sealed class VerifiedModel : AuthPageModel
{
    /// <summary>Renders the confirmation.</summary>
    public void OnGet()
    {
    }
}
