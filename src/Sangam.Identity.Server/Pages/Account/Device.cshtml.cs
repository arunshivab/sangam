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
    private readonly IOrgChoiceStore _orgChoices;
    private ClaimsPrincipal? _session;

    /// <summary>Initialises the page.</summary>
    /// <param name="accounts">Accounts.</param>
    /// <param name="apps">Applications.</param>
    /// <param name="consents">Consents.</param>
    /// <param name="tenancy">Tenancy.</param>
    /// <param name="policies">Sign-in policies.</param>
    /// <param name="scopes">OpenIddict scopes.</param>
    /// <param name="audit">Audit writer.</param>
    /// <param name="orgChoices">rc.13: the branch the person last chose in the application.</param>
    public DeviceModel(IAccountService accounts, IAppDirectory apps, IConsentService consents, ITenancyQuery tenancy, ISecurityPolicyService policies, IOpenIddictScopeManager scopes, IAuditWriter audit, IOrgChoiceStore orgChoices)
    {
        _orgChoices = orgChoices ?? throw new ArgumentNullException(nameof(orgChoices));
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

    /// <summary>
    /// rc.13: the organisations the person may be working at, when the application asked for them and there is more than one
    /// (the same question as "Working at which branch?" in an ordinary sign-in).
    /// </summary>
    public IReadOnlyList<OrgClaim> Branches { get; private set; } = [];

    /// <summary>rc.13: the branch chosen (or, on first showing, the one last used).</summary>
    [BindProperty]
    public Guid? OrgId { get; set; }

    /// <summary>rc.13: whether the person tried to allow without choosing a branch.</summary>
    public bool ChooseBranch { get; private set; }

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
        IReadOnlyList<OrgClaim> orgs = await _tenancy.GetOrgClaimsAsync(User.Id, App.Id, cancellationToken);

        // rc.13: the device's token says where the person is working, as an ordinary sign-in's does.
        OrgClaim? current = null;
        if (requested.Contains(Sangam.Shared.Constants.SangamScopes.OrgsRead) || requested.Contains(Sangam.Shared.Constants.SangamScopes.OrgCurrent))
        {
            current = orgs.Count == 1 ? orgs[0] : orgs.FirstOrDefault(o => o.Id == OrgId);
            if (current is null && orgs.Count > 1)
            {
                ChooseBranch = true;
                Step = DeviceStep.Approve;
                return Page();
            }

            if (current is not null && orgs.Count > 1)
            {
                await _orgChoices.RememberAsync(User.Id, App.Id, current.Id, cancellationToken);
            }
        }

        await _consents.GrantAsync(User.Id, App.Id, requested, ClientIp, ClientUserAgent, cancellationToken: cancellationToken);
        IReadOnlyDictionary<string, object>? custom = requested.Contains(Sangam.Shared.Constants.SangamScopes.Attributes)
            ? await HttpContext.RequestServices.GetRequiredService<Sangam.Identity.Application.Attributes.IAttributeService>().ClaimsAsync(User.Id, App.Id, cancellationToken)
            : null;
        // rc.14: the device knows the person by the application's own id.
        Guid subject = await HttpContext.RequestServices.GetRequiredService<Sangam.Identity.Application.Apps.ISubjectIds>().SubjectAsync(App.Id, User.Id, cancellationToken);
        ClaimsIdentity identity = SangamClaimsBuilder.Build(User, subject, requested, orgs, TokenValidationParameters.DefaultAuthenticationType, SangamAuthentication.SessionId(_session!) is Guid browser ? AppSessionIds.For(browser, App.Id, App.IsPlatform).ToString("D") : null, AuthenticationProof.FromSession(_session!), custom, current, internalSessionId: SangamAuthentication.SessionId(_session!)?.ToString("D"));
        List<string> resources = [];
        await foreach (string resource in _scopes.ListResourcesAsync(identity.GetScopes(), cancellationToken))
        {
            resources.Add(resource);
        }

        identity.SetResources(resources);

        // rc.14: the authorization is kept under the person's Sangam id (as a browser sign-in's is), so leaving the
        // application, inactivity and account merge find and revoke it; the tokens carry the application's own id.
        IOpenIddictApplicationManager applications = HttpContext.RequestServices.GetRequiredService<IOpenIddictApplicationManager>();
        IOpenIddictAuthorizationManager authorizations = HttpContext.RequestServices.GetRequiredService<IOpenIddictAuthorizationManager>();
        object client = await applications.FindByClientIdAsync(App.ClientId, cancellationToken) ?? throw new InvalidOperationException("The OpenIddict client is missing.");
        string applicationId = (await applications.GetIdAsync(client, cancellationToken))!;
        object? authorization = null;
        await foreach (object candidate in authorizations.FindAsync(User.Id.ToString("D"), applicationId, Statuses.Valid, AuthorizationTypes.Permanent, requested, cancellationToken))
        {
            authorization = candidate;
            break;
        }

        authorization ??= await authorizations.CreateAsync(identity, User.Id.ToString("D"), applicationId, AuthorizationTypes.Permanent, requested, cancellationToken);
        identity.SetAuthorizationId(await authorizations.GetIdAsync(authorization, cancellationToken));

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
        if (asked.Contains(Sangam.Shared.Constants.SangamScopes.OrgsRead) || asked.Contains(Sangam.Shared.Constants.SangamScopes.OrgCurrent))
        {
            IReadOnlyList<OrgClaim> orgs = await _tenancy.GetOrgClaimsAsync(User.Id, App.Id, cancellationToken);
            Branches = orgs.Count > 1 ? orgs : [];
            if (Branches.Count > 0 && OrgId is null && await _orgChoices.LastAsync(User.Id, App.Id, cancellationToken) is Guid last && orgs.Any(o => o.Id == last))
            {
                OrgId = last;
            }
        }

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
