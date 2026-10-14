using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Sangam.Identity.Application.Apps;
using Sangam.Identity.Application.Tenancy;
using Sangam.Identity.Server.Authentication;
using Sangam.Identity.Server.Authorization;

namespace Sangam.Identity.Server.Pages.Account;

/// <summary>
/// rc.12 (A6; founder decisions, 14 Oct 2026): "Working at which branch?", for a person with two or more organisations in
/// the application. Cards, last used first then A to Z, with a search above six; the choice can be remembered on this device
/// for twelve hours, for this person only.
/// </summary>
public sealed class SelectOrgModel : AuthPageModel
{
    /// <summary>Above this many, a search is offered.</summary>
    public const int SearchAbove = 6;

    private readonly IAppDirectory _apps;
    private readonly ITenancyQuery _tenancy;
    private readonly IOrgChoiceStore _choices;
    private readonly IDataProtectionProvider _protection;

    /// <summary>Initialises the page.</summary>
    /// <param name="apps">App directory.</param>
    /// <param name="tenancy">The person's organisations.</param>
    /// <param name="choices">The last choice.</param>
    /// <param name="protection">Data protection, for the choice cookie.</param>
    public SelectOrgModel(IAppDirectory apps, ITenancyQuery tenancy, IOrgChoiceStore choices, IDataProtectionProvider protection)
    {
        _apps = apps ?? throw new ArgumentNullException(nameof(apps));
        _tenancy = tenancy ?? throw new ArgumentNullException(nameof(tenancy));
        _choices = choices ?? throw new ArgumentNullException(nameof(choices));
        _protection = protection ?? throw new ArgumentNullException(nameof(protection));
    }

    /// <summary>The authorization request to go back to.</summary>
    [BindProperty(SupportsGet = true)]
    public string ReturnUrl { get; set; } = string.Empty;

    /// <summary>The search, when there are many.</summary>
    [BindProperty(SupportsGet = true)]
    public string? Q { get; set; }

    /// <summary>The organisation chosen.</summary>
    [BindProperty]
    public Guid OrgId { get; set; }

    /// <summary>Remember on this device for 12 hours.</summary>
    [BindProperty]
    public bool Remember { get; set; }

    /// <summary>The application.</summary>
    public AppSummary App { get; private set; } = null!;

    /// <summary>Every organisation, ordered: last used first, then A to Z.</summary>
    public IReadOnlyList<OrgClaim> All { get; private set; } = [];

    /// <summary>The organisations shown (after the search).</summary>
    public IReadOnlyList<OrgClaim> Shown { get; private set; } = [];

    /// <summary>The one used last, if any.</summary>
    public Guid? LastUsed { get; private set; }

    /// <summary>Shows the choice.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        return await LoadAsync(cancellationToken) ? Page() : RedirectToPage("/Account/Home");
    }

    /// <summary>Takes the choice and goes back to the application's sign-in.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        if (!await LoadAsync(cancellationToken))
        {
            return RedirectToPage("/Account/Home");
        }

        // Only an organisation the person works at in this application.
        if (All.All(o => o.Id != OrgId))
        {
            return Page();
        }

        Guid userId = SangamAuthentication.UserId(User)!.Value;
        OrgChoice.Write(HttpContext, _protection, userId, App.Id, OrgId, Remember);
        await _choices.RememberAsync(userId, App.Id, OrgId, cancellationToken);
        return LocalRedirect(OrgChoice.WithoutSelectPrompt(ReturnUrl));
    }

    private async Task<bool> LoadAsync(CancellationToken cancellationToken)
    {
        Guid? userId = SangamAuthentication.UserId(User);
        if (userId is null || !Url.IsLocalUrl(ReturnUrl) || await PartnerContext.ResolveAsync(_apps, ReturnUrl, cancellationToken) is not AppSummary app)
        {
            return false;
        }

        App = app;
        await ResolvePartnerAsync(_apps, ReturnUrl, cancellationToken);
        IReadOnlyList<OrgClaim> orgs = await _tenancy.GetOrgClaimsAsync(userId.Value, app.Id, cancellationToken);
        LastUsed = await _choices.LastAsync(userId.Value, app.Id, cancellationToken);
        All = [.. orgs.OrderBy(o => o.Id == LastUsed ? 0 : 1).ThenBy(o => o.Name, StringComparer.CurrentCultureIgnoreCase)];
        string? q = Q?.Trim();
        Shown = string.IsNullOrEmpty(q) ? All : [.. All.Where(o => o.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase))];
        return All.Count > 0;
    }
}
