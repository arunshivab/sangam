using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Sangam.Identity.Application.Accounts;
using Sangam.Identity.Application.Apps;
using Sangam.Identity.Application.Consents;
using Sangam.Identity.Application.Tenancy;
using Sangam.Identity.Domain.Entities;
using Sangam.Identity.Infrastructure.Saml;
using Sangam.Identity.Server.Authentication;
using Sangam.Identity.Server.Authorization;
using Sangam.Identity.Server.Saml;
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
    private readonly SamlIdentityProvider _saml;

    /// <summary>Initialises the page.</summary>
    public ConsentModel(IAccountService accounts, IAppDirectory apps, IConsentService consents, ITenancyQuery tenancy, SamlIdentityProvider saml)
    {
        _saml = saml ?? throw new ArgumentNullException(nameof(saml));
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

        await _consents.GrantAsync(User.Id, App.Id, Scopes, ClientIp, ClientUserAgent, cancellationToken: cancellationToken);
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

        // V-14: a SAML service provider is told exactly its released attributes, so the list comes from those.
        SamlServiceProvider? provider = ReturnUrl.StartsWith("/saml/continue", StringComparison.OrdinalIgnoreCase)
            ? await _saml.FindByAppAsync(app.Id, cancellationToken)
            : null;
        IReadOnlyDictionary<string, object>? custom = requested.Contains(SangamScopes.Attributes)
            ? await HttpContext.RequestServices.GetRequiredService<Sangam.Identity.Application.Attributes.IAttributeService>().ClaimsAsync(user.Id, app.Id, cancellationToken)
            : null;
        Shared = provider is null
            ? await DescribeAsync(L, user, app, requested, _tenancy, cancellationToken, custom)
            : SamlRelease.Describe(L, provider, user, await _tenancy.GetOrgClaimsAsync(user.Id, app.Id, cancellationToken));
        return true;
    }

    /// <summary>What a person shares with an application for these scopes, with their real values (also the /device page).</summary>
    /// <param name="text">The text catalogue.</param>
    /// <param name="user">The person.</param>
    /// <param name="app">The application.</param>
    /// <param name="requested">The scopes asked for.</param>
    /// <param name="tenancy">Tenancy, for organisations and roles.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="custom">PR-25: the application's custom claims, for the attributes scope.</param>
    internal static async Task<IReadOnlyList<(string Label, string Value)>> DescribeAsync(Microsoft.Extensions.Localization.IStringLocalizer text, UserSummary user, AppSummary app, IReadOnlySet<string> requested, ITenancyQuery tenancy, CancellationToken cancellationToken, IReadOnlyDictionary<string, object>? custom = null)
    {
#pragma warning disable IDE1006 // Named L, as on every page, so the i18n lint finds the keys below.
        Microsoft.Extensions.Localization.IStringLocalizer L = text;
#pragma warning restore IDE1006
        List<(string, string)> shared = [];
        if (requested.Contains(SangamScopes.Profile))
        {
            shared.Add((L["Your name"], user.DisplayName));
            shared.Add((L["Date of birth and gender"], user.DateOfBirth.ToString("d MMM yyyy", CultureInfo.CurrentCulture) + " · " + GenderText.Label(L, user.Gender)));
        }

        if (requested.Contains(SangamScopes.Email))
        {
            shared.Add((L["Email address"], user.Email));
        }

        if (requested.Contains(SangamScopes.Phone))
        {
            shared.Add((L["Mobile number"], user.Mobile ?? "—"));
        }

        if (requested.Contains(SangamScopes.OrgsRead))
        {
            IReadOnlyList<OrgClaim> orgs = await tenancy.GetOrgClaimsAsync(user.Id, app.Id, cancellationToken);
            shared.Add((L["Organisations and roles"], orgs.Count == 0 ? L["None yet"] : string.Join(", ", orgs.Select(o => o.Name + " (" + o.Role + ")"))));
        }

        if (requested.Contains(SangamScopes.Attributes))
        {
            string details = custom is null || custom.Count == 0
                ? L["None yet"]
                : string.Join(" · ", custom.Select(c => c.Key + ": " + (c.Value is string[] list ? string.Join(", ", list) : Convert.ToString(c.Value, CultureInfo.InvariantCulture))));
            shared.Add((L["Details {0} keeps about you", app.DisplayName], details));
        }

        if (requested.Contains(SangamScopes.OfflineAccess))
        {
            shared.Add((L["Stay signed in"], L["The app can refresh its access without asking you again"]));
        }

        return shared;
    }
}
