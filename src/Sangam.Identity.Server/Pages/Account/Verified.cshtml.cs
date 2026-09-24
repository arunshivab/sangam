using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Sangam.Identity.Application.Apps;

namespace Sangam.Identity.Server.Pages.Account;

/// <summary>Screen 4 — email verified, with "Continue to …" when an app started the flow.</summary>
[Authorize]
public sealed class VerifiedModel : AuthPageModel
{
    private readonly IAppDirectory _apps;

    /// <summary>Initialises the page.</summary>
    /// <param name="apps">App directory.</param>
    public VerifiedModel(IAppDirectory apps)
    {
        _apps = apps ?? throw new ArgumentNullException(nameof(apps));
    }

    /// <summary>The app's authorization request to continue to, if any.</summary>
    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    /// <summary>Where the primary button goes.</summary>
    public string ContinueUrl => Partner is null ? "/account" : SafeReturnUrl(ReturnUrl);

    /// <summary>Renders the confirmation.</summary>
    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        await ResolvePartnerAsync(_apps, ReturnUrl, cancellationToken);
    }
}
