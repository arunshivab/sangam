using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Sangam.Identity.Application.Accounts;
using Sangam.Identity.Application.Apps;
using Sangam.Identity.Application.Consents;
using Sangam.Identity.Application.Tenancy;
using Sangam.Identity.Server.Authentication;
using Sangam.Identity.Server.Authorization;
using Sangam.Shared.Constants;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Sangam.Identity.Server.Pages.Account;

/// <summary>Screen 7 — the trust moment. Shows the real values that will be shared and records the decision.</summary>
[Authorize]
public sealed class ConsentModel : AuthPageModel
{
    private readonly IAccountService _accounts;
    private readonly IAppDirectory _apps;
    private readonly IConsentService _consents;
    private readonly ITenancyQuery _tenancy;

    /// <summary>Initialises the page.</summary>
    public ConsentModel(IAccountService accounts, IAppDirectory apps, IConsentService consents, ITenancyQuery tenancy)
    {
        _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
        _apps = apps ?? throw new ArgumentNullException(nameof(apps));
        _consents = consents ?? throw new ArgumentNullException(nameof(consents));
        _tenancy = tenancy ?? throw new ArgumentNullException(nameof(tenancy));
    }

    /// <summary>The authorization request to return to.</summary>
    [BindProperty(SupportsGet = true)]
    public string ReturnUrl { get; set; } = string.Empty;

    /// <summary>The requesting app.</summary>
    public AppSummary App { get; private set; } = null!;

    /// <summary>The signed-in user.</summary>
    public new UserSummary User { get; private set; } = null!;

    /// <summary>Two-letter initials for the avatar.</summary>
    public string Initials { get; private set; } = string.Empty;

    /// <summary>Requested scopes (for the chips).</summary>
    public IReadOnlyList<string> Scopes { get; private set; } = [];

    /// <summary>Claim label + real value rows.</summary>
    public IReadOnlyList<(string Label, string Value)> Shared { get; private set; } = [];

    /// <summary>Renders the consent screen.</summary>
    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        if (!await LoadAsync(cancellationToken))
        {
            return RedirectToPage("/Account/Home");
        }

        return Page();
    }

    /// <summary>Records the grant and returns to the authorization endpoint.</summary>
    public async Task<IActionResult> OnPostAllowAsync(CancellationToken cancellationToken)
    {
        if (!await LoadAsync(cancellationToken))
        {
            return RedirectToPage("/Account/Home");
        }

        await _consents.GrantAsync(User.Id, App.Id, Scopes, ClientIp, ClientUserAgent, cancellationToken);
        return LocalRedirect(ReturnUrl);
    }

    /// <summary>Records the denial and returns to the authorization endpoint, which answers the app with <c>access_denied</c>.</summary>
    public async Task<IActionResult> OnPostDenyAsync(CancellationToken cancellationToken)
    {
        if (!await LoadAsync(cancellationToken))
        {
            return RedirectToPage("/Account/Home");
        }

        await _consents.DenyAsync(User.Id, App.Id, ClientIp, cancellationToken);
        await SangamAuthentication.StorePendingConsentDeniedAsync(HttpContext, App.ClientId);
        return LocalRedirect(ReturnUrl);
    }

    private async Task<bool> LoadAsync(CancellationToken cancellationToken)
    {
        if (!Url.IsLocalUrl(ReturnUrl))
        {
            return false;
        }

        AppSummary? app = await PartnerContext.ResolveAsync(_apps, ReturnUrl, cancellationToken);
        Guid? userId = SangamAuthentication.UserId(base.User);
        UserSummary? user = userId is null ? null : await _accounts.FindByIdAsync(userId.Value, cancellationToken);
        if (app is null || user is null)
        {
            return false;
        }

        App = app;
        User = user;
        Initials = ((user.FirstName.Length > 0 ? user.FirstName[..1] : "") + (user.LastName.Length > 0 ? user.LastName[..1] : "")).ToUpperInvariant();

        int q = ReturnUrl.IndexOf('?', StringComparison.Ordinal);
        Dictionary<string, Microsoft.Extensions.Primitives.StringValues> query = QueryHelpers.ParseQuery(q < 0 ? string.Empty : ReturnUrl[q..]);
        string scopeParam = query.TryGetValue(Parameters.Scope, out Microsoft.Extensions.Primitives.StringValues v) ? v.ToString() : string.Empty;
        HashSet<string> requested = [.. scopeParam.Split(' ', StringSplitOptions.RemoveEmptyEntries)];
        Scopes = [.. SangamScopes.UserScopes.Where(requested.Contains)];

        List<(string, string)> shared = [];
        if (requested.Contains(SangamScopes.Profile))
        {
            shared.Add(("Your name", user.DisplayName));
            shared.Add(("Date of birth and gender", user.DateOfBirth.ToString("d MMM yyyy", CultureInfo.InvariantCulture) + " · " + Domain.Genders.ToCode(user.Gender).Replace('_', ' ')));
        }

        if (requested.Contains(SangamScopes.Email))
        {
            shared.Add(("Email address", user.Email));
        }

        if (requested.Contains(SangamScopes.Phone))
        {
            shared.Add(("Mobile number", user.Mobile ?? "—"));
        }

        if (requested.Contains(SangamScopes.OrgsRead))
        {
            IReadOnlyList<OrgClaim> orgs = await _tenancy.GetOrgClaimsAsync(user.Id, app.Id, cancellationToken);
            shared.Add(("Organisations and roles", orgs.Count == 0 ? "None yet" : string.Join(", ", orgs.Select(o => o.Name + " (" + o.Role + ")"))));
        }

        if (requested.Contains(SangamScopes.OfflineAccess))
        {
            shared.Add(("Stay signed in", "The app can refresh its access without asking you again"));
        }

        Shared = shared;
        return true;
    }
}
