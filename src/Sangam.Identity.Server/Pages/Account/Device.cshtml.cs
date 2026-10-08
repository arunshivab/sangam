using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using Sangam.Identity.Application.Abstractions;
using Sangam.Identity.Application.Accounts;
using Sangam.Identity.Application.Apps;
using Sangam.Identity.Application.Consents;
using Sangam.Identity.Application.Security;
using Sangam.Identity.Application.Tenancy;
using Sangam.Identity.Domain;
using Sangam.Identity.Domain.Enums;
using Sangam.Identity.Server.Authentication;
using Sangam.Identity.Server.Authorization;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Sangam.Identity.Server.Pages.Account;

/// <summary>
/// PR-21, the device authorization grant (RFC 8628, SGM-219): a TV, kiosk or command line shows a code and this
/// address; the person opens it on their phone or computer, signs in as usual (with the application's sign-in rules),
/// checks the code matches, sees exactly what will be shared, and allows or refuses. Allowing is their consent.
/// </summary>
[AllowAnonymous]
public sealed class DeviceModel : AuthPageModel
{
    private readonly IAccountService _accounts;
    private readonly IAppDirectory _apps;
    private readonly IConsentService _consents;
    private readonly ITenancyQuery _tenancy;
    private readonly ISecurityPolicyService _policies;
    private readonly IOpenIddictScopeManager _scopes;
    private readonly IAuditWriter _audit;
    private ClaimsPrincipal? _session;

    /// <summary>Initialises the page.</summary>
    /// <param name="accounts">Accounts.</param>
    /// <param name="apps">Applications.</param>
    /// <param name="consents">Consents.</param>
    /// <param name="tenancy">Tenancy.</param>
    /// <param name="policies">Sign-in policies.</param>
    /// <param name="scopes">OpenIddict scopes.</param>
    /// <param name="audit">Audit writer.</param>
    public DeviceModel(IAccountService accounts, IAppDirectory apps, IConsentService consents, ITenancyQuery tenancy, ISecurityPolicyService policies, IOpenIddictScopeManager scopes, IAuditWriter audit)
    {
        _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
        _apps = apps ?? throw new ArgumentNullException(nameof(apps));
        _consents = consents ?? throw new ArgumentNullException(nameof(consents));
        _tenancy = tenancy ?? throw new ArgumentNullException(nameof(tenancy));
        _policies = policies ?? throw new ArgumentNullException(nameof(policies));
        _scopes = scopes ?? throw new ArgumentNullException(nameof(scopes));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
    }

    /// <summary>What the page shows.</summary>
    public DeviceStep Step { get; private set; } = DeviceStep.EnterCode;

    /// <summary>The code the person typed or followed.</summary>
    [BindProperty(SupportsGet = true, Name = "user_code")]
    public string? UserCode { get; set; }

    /// <summary>The outcome after allowing or refusing (<c>approved</c>, <c>refused</c>).</summary>
    [BindProperty(SupportsGet = true, Name = "result")]
    public string? Result { get; set; }

    /// <summary>The application asking.</summary>
    public AppSummary? App { get; private set; }

    /// <summary>The signed-in person.</summary>
    public new UserSummary? User { get; private set; }

    /// <summary>What will be shared, with the real values.</summary>
    public IReadOnlyList<(string Label, string Value)> Shared { get; private set; } = [];

    /// <summary>Shows the code form, the approval, or the outcome.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        if (Result is "approved" or "refused")
        {
            Step = Result == "approved" ? DeviceStep.Approved : DeviceStep.Refused;
            return Page();
        }

        if (string.IsNullOrWhiteSpace(UserCode))
        {
            return Page();
        }

        (IActionResult? redirect, ClaimsPrincipal? request) = await LoadAsync(cancellationToken);
        if (redirect is not null)
        {
            return redirect;
        }

        Step = request is null ? DeviceStep.BadCode : DeviceStep.Approve;
        return Page();
    }

    /// <summary>Allows the device: records consent and finishes the sign-in on the device.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<IActionResult> OnPostAllowAsync(CancellationToken cancellationToken)
    {
        (IActionResult? redirect, ClaimsPrincipal? request) = await LoadAsync(cancellationToken);
        if (redirect is not null)
        {
            return redirect;
        }

        if (request is null || App is null || User is null)
        {
            Step = DeviceStep.BadCode;
            return Page();
        }

        System.Collections.Immutable.ImmutableArray<string> requested = request.GetScopes();
        await _consents.GrantAsync(User.Id, App.Id, requested, ClientIp, ClientUserAgent, cancellationToken: cancellationToken);
        IReadOnlyList<OrgClaim> orgs = await _tenancy.GetOrgClaimsAsync(User.Id, App.Id, cancellationToken);
        IReadOnlyDictionary<string, object>? custom = requested.Contains(Sangam.Shared.Constants.SangamScopes.Attributes)
            ? await HttpContext.RequestServices.GetRequiredService<Sangam.Identity.Application.Attributes.IAttributeService>().ClaimsAsync(User.Id, App.Id, cancellationToken)
            : null;
        ClaimsIdentity identity = SangamClaimsBuilder.Build(User, requested, orgs, TokenValidationParameters.DefaultAuthenticationType, SangamAuthentication.SessionId(_session!)?.ToString("D"), AuthenticationProof.FromSession(_session!), custom);
        List<string> resources = [];
        await foreach (string resource in _scopes.ListResourcesAsync(identity.GetScopes(), cancellationToken))
        {
            resources.Add(resource);
        }

        identity.SetResources(resources);
        await _audit.WriteAsync(
            new AuditEntry(AuditActions.DeviceApprove, AuditActorType.User, User.Id, App.Id, "app", App.Id, IpAddress: ClientIp, UserAgent: ClientUserAgent),
            cancellationToken);
        return SignIn(new ClaimsPrincipal(identity), new AuthenticationProperties { RedirectUri = "/device?result=approved" }, OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    /// <summary>Refuses the device: it is told access was denied.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<IActionResult> OnPostDenyAsync(CancellationToken cancellationToken)
    {
        (IActionResult? redirect, ClaimsPrincipal? request) = await LoadAsync(cancellationToken);
        if (redirect is not null)
        {
            return redirect;
        }

        if (request is not null && App is not null && User is not null)
        {
            await _audit.WriteAsync(new AuditEntry(AuditActions.DeviceDeny, AuditActorType.User, User.Id, App.Id, "app", App.Id, IpAddress: ClientIp), cancellationToken);
        }

        return Forbid(new AuthenticationProperties { RedirectUri = "/device?result=refused" }, OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    /// <summary>
    /// Reads the device's request behind the code (OpenIddict validates the code), then the person: signed in, and
    /// signed in strongly enough for this application. Returns a redirect to sign in when needed.
    /// </summary>
    private async Task<(IActionResult? Redirect, ClaimsPrincipal? Request)> LoadAsync(CancellationToken cancellationToken)
    {
        AuthenticateResult result = await HttpContext.AuthenticateAsync(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        ClaimsPrincipal? request = result.Succeeded ? result.Principal : null;
        string? clientId = request?.GetClaim(Claims.ClientId);
        App = clientId is null ? null : await _apps.FindByClientIdAsync(clientId, cancellationToken);
        if (request is null || App is null || App.Status != AppStatus.Active)
        {
            return (null, null);
        }

        string returnUrl = "/device?user_code=" + Uri.EscapeDataString(UserCode ?? string.Empty);
        (UserSummary? user, ClaimsPrincipal? session, string? redirect) = await SignInGate.CheckAsync(HttpContext, _accounts, _policies, App.Id, returnUrl, cancellationToken: cancellationToken);
        if (redirect is not null)
        {
            return (Redirect(redirect), null);
        }

        User = user!;
        _session = session;
        HashSet<string> asked = request.GetScopes().ToHashSet(StringComparer.Ordinal);
        IReadOnlyDictionary<string, object>? custom = asked.Contains(Sangam.Shared.Constants.SangamScopes.Attributes)
            ? await HttpContext.RequestServices.GetRequiredService<Sangam.Identity.Application.Attributes.IAttributeService>().ClaimsAsync(User.Id, App.Id, cancellationToken)
            : null;
        Shared = await ConsentModel.DescribeAsync(L, User, App, asked, _tenancy, cancellationToken, custom);
        return (null, request);
    }
}

/// <summary>What the device page shows.</summary>
public enum DeviceStep
{
    /// <summary>Asks for the code.</summary>
    EnterCode,

    /// <summary>The code is wrong, used or expired.</summary>
    BadCode,

    /// <summary>Shows who is asking and what would be shared.</summary>
    Approve,

    /// <summary>Allowed: go back to the device.</summary>
    Approved,

    /// <summary>Refused.</summary>
    Refused,
}
