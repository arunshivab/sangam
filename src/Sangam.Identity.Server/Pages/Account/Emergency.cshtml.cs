using Microsoft.AspNetCore.Mvc;
using Sangam.Identity.Application.Accounts;
using Sangam.Identity.Application.Apps;
using Sangam.Identity.Application.Tenancy;
using Sangam.Identity.Domain;
using Sangam.Identity.Domain.Enums;
using Sangam.Identity.Server.Authentication;
using Sangam.Identity.Server.Authorization;
using Sangam.Shared.Constants;

namespace Sangam.Identity.Server.Pages.Account;

/// <summary>
/// rc.12 (M12): "Emergency access" — the application asked for it with its own case reference. The person has just proved it
/// is them again (a passkey or an authenticator app); they give a reason, and for up to 60 minutes the application's tokens
/// carry the emergency marker. Every use is recorded and the hospital's reviewers are told at once.
/// </summary>
public sealed class EmergencyModel : AuthPageModel
{
    private static readonly TimeSpan Freshness = TimeSpan.FromMinutes(5);
    private readonly IAppDirectory _apps;
    private readonly IEmergencyAccessService _emergencies;
    private readonly ITenancyQuery _tenancy;
    private readonly IAccountService _accounts;

    /// <summary>Initialises the page.</summary>
    /// <param name="apps">App directory.</param>
    /// <param name="emergencies">Emergency access.</param>
    /// <param name="tenancy">The person's organisations.</param>
    /// <param name="accounts">Accounts.</param>
    public EmergencyModel(IAppDirectory apps, IEmergencyAccessService emergencies, ITenancyQuery tenancy, IAccountService accounts)
    {
        _apps = apps ?? throw new ArgumentNullException(nameof(apps));
        _emergencies = emergencies ?? throw new ArgumentNullException(nameof(emergencies));
        _tenancy = tenancy ?? throw new ArgumentNullException(nameof(tenancy));
        _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
    }

    /// <summary>The authorization request to go back to.</summary>
    [BindProperty(SupportsGet = true)]
    public string ReturnUrl { get; set; } = string.Empty;

    /// <summary>The organisation the person is working at.</summary>
    [BindProperty(SupportsGet = true)]
    public Guid? Org { get; set; }

    /// <summary>Why.</summary>
    [BindProperty]
    public string Reason { get; set; } = string.Empty;

    /// <summary>The application.</summary>
    public AppSummary App { get; private set; } = null!;

    /// <summary>The application's case reference.</summary>
    public string CaseRef { get; private set; } = string.Empty;

    /// <summary>The organisation's name.</summary>
    public string? OrgName { get; private set; }

    /// <summary>Why emergency access cannot be given, when it cannot.</summary>
    public string? Refusal { get; private set; }

    /// <summary>The form's error.</summary>
    public string? Error { get; private set; }

    /// <summary>Shows the reason form, or why it cannot be given.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The page.</returns>
    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
        => await LoadAsync(cancellationToken) ? Page() : RedirectToPage("/Account/Home");

    /// <summary>Starts emergency access and goes back to the application.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The application's sign-in, or the page with what went wrong.</returns>
    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        if (!await LoadAsync(cancellationToken))
        {
            return RedirectToPage("/Account/Home");
        }

        if (Refusal is not null)
        {
            return Page();
        }

        string method = User.FindFirst(SangamAuthentication.SessionModeClaim)?.Value == SignInModes.ToCode(SignInMode.Passkey) ? "passkey" : "totp";
        EmergencyStartResult result = await _emergencies.StartAsync(
            new EmergencyRequest(SangamAuthentication.UserId(User)!.Value, App.Id, Org!.Value, SangamAuthentication.SessionId(User), CaseRef, Reason, method),
            cancellationToken);
        switch (result.Status)
        {
            case EmergencyStartStatus.Started:
                return LocalRedirect(ReturnUrl);
            case EmergencyStartStatus.ReasonRequired:
                Error = L["Give the reason in a few words; your hospital's reviewers read it."];
                return Page();
            default:
                Refusal = RefusalText(result.Status);
                return Page();
        }
    }

    /// <summary>Goes back to the application without emergency access.</summary>
    /// <returns>The application's sign-in.</returns>
    public IActionResult OnPostCancel()
        => Url.IsLocalUrl(ReturnUrl) ? LocalRedirect(WithoutEmergency(ReturnUrl)) : RedirectToPage("/Account/Home");

    /// <summary>The return address without the emergency request.</summary>
    /// <param name="returnUrl">The authorization request.</param>
    /// <returns>The same request, asking for nothing more.</returns>
    public static string WithoutEmergency(string returnUrl)
    {
        ArgumentNullException.ThrowIfNull(returnUrl);
        int q = returnUrl.IndexOf('?', StringComparison.Ordinal);
        if (q < 0)
        {
            return returnUrl;
        }

        IEnumerable<string> kept = returnUrl[(q + 1)..].Split('&').Where(p => !p.StartsWith(SangamClaims.Emergency + "=", StringComparison.Ordinal));
        return returnUrl[..q] + "?" + string.Join('&', kept);
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
        int q = ReturnUrl.IndexOf('?', StringComparison.Ordinal);
        CaseRef = q < 0 ? string.Empty : System.Web.HttpUtility.ParseQueryString(ReturnUrl[(q + 1)..])[SangamClaims.Emergency] ?? string.Empty;
        IReadOnlyList<OrgClaim> orgs = await _tenancy.GetOrgClaimsAsync(userId.Value, app.Id, cancellationToken);
        OrgClaim? org = orgs.FirstOrDefault(o => o.Id == Org) ?? (orgs.Count == 1 ? orgs[0] : null);
        Org = org?.Id;
        OrgName = org?.Name;

        UserSummary? user = await _accounts.FindByIdAsync(userId.Value, cancellationToken);
        bool fresh = SangamAuthentication.HasSecondFactor(User)
            && SangamAuthentication.AuthenticatedAt(User) is DateTimeOffset at
            && DateTimeOffset.UtcNow - at <= Freshness;
        if (string.IsNullOrEmpty(CaseRef))
        {
            Refusal = RefusalText(EmergencyStartStatus.CaseRequired);
        }
        else if (org is null)
        {
            Refusal = RefusalText(EmergencyStartStatus.NotStaff);
        }
        else if (!fresh)
        {
            Refusal = user?.MfaEnrolled == true
                ? L["Sign in again with your passkey or authenticator app first."]
                : L["Emergency access needs a passkey or an authenticator app, so Sangam can check it is you on the spot. Set one up in your Sangam account when there is time; for now, work through the staff on duty."];
        }
        else if (!await _emergencies.IsOnAsync(app.Id, org.Id, cancellationToken))
        {
            Refusal = RefusalText(EmergencyStartStatus.SwitchedOff);
        }

        return true;
    }

    private string RefusalText(EmergencyStartStatus status) => status switch
    {
        EmergencyStartStatus.NotStaff => L["Emergency access is only for staff of this hospital. Work through the staff on duty; they can enter verbal orders under their own login."],
        EmergencyStartStatus.SwitchedOff => L["{0} has not switched emergency access on. Ask your hospital's admin.", OrgName ?? App.DisplayName],
        EmergencyStartStatus.DailyLimit => L["You have used emergency access three times today, the most allowed. Ask a colleague on duty, or your hospital's admin."],
        _ => L["{0} did not say which case this is for. Go back and try again from the patient's record.", App.DisplayName],
    };
}
