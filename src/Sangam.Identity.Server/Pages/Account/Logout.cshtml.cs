using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using Sangam.Identity.Application.Abstractions;
using Sangam.Identity.Application.Accounts;
using Sangam.Identity.Application.Apps;
using Sangam.Identity.Application.Portal;
using Sangam.Identity.Domain;
using Sangam.Identity.Domain.Enums;
using Sangam.Identity.Server.Authentication;

namespace Sangam.Identity.Server.Pages.Account;

/// <summary>
/// Screen 8 — sign out. Serves two entrances: the user's own <c>/logout</c>, and the OpenIddict
/// end-session endpoint (<c>/connect/endsession</c>, passthrough) when an app initiates it with
/// <c>id_token_hint</c> and <c>post_logout_redirect_uri</c>. Signing out ends the Sangam session;
/// the app-initiated variant also lets OpenIddict redirect back to the app.
/// </summary>
public sealed class LogoutModel : AuthPageModel
{
    private readonly IAccountService _accounts;
    private readonly IAppDirectory _apps;
    private readonly IAuditWriter _audit;
    private readonly ISessionService _sessions;

    /// <summary>Initialises the page.</summary>
    public LogoutModel(IAccountService accounts, IAppDirectory apps, IAuditWriter audit, ISessionService sessions)
    {
        _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
        _apps = apps ?? throw new ArgumentNullException(nameof(apps));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
    }

    /// <summary>For app-initiated sign-out: the end-session parameters, re-posted so OpenIddict can validate the POST too.</summary>
    public IReadOnlyDictionary<string, string> EndSessionFields { get; private set; } = new Dictionary<string, string>();

    /// <summary>The signed-in user, if any.</summary>
    public new UserSummary? User { get; private set; }

    /// <summary>Initials for the avatar.</summary>
    public string Initials { get; private set; } = string.Empty;

    /// <summary>The app that initiated sign-out, if any.</summary>
    public new AppSummary? Partner { get; private set; }

    /// <summary>Where "Return to … without signing out" goes (the app's registered post-logout URI).</summary>
    public string? PartnerReturnUrl { get; private set; }

    /// <summary>Where "Stay signed in" goes.</summary>
    public string StayUrl => PartnerReturnUrl ?? "/account";

    /// <summary>Renders the confirmation. Anonymous visitors on the plain <c>/logout</c> go to sign-in.</summary>
    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        await LoadAsync(cancellationToken);
        if (User is null && Partner is null)
        {
            return RedirectToPage("/Account/Login");
        }

        return Page();
    }

    /// <summary>Ends the session (and, for app-initiated sign-out, lets OpenIddict redirect to the app).</summary>
    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        await LoadAsync(cancellationToken);
        Guid? id = SangamAuthentication.UserId(base.User);
        Guid? sessionId = SangamAuthentication.SessionId(base.User);
        if (sessionId is not null)
        {
            await _sessions.EndAsync(sessionId.Value, "user", cancellationToken);
        }

        await HttpContext.SignOutAsync(IdentityConstants.ApplicationScheme);
        await SangamAuthentication.ClearPendingAsync(HttpContext);

        if (id is not null)
        {
            string action = Partner is null ? AuditActions.UserLogout : AuditActions.UserLogoutApp;
            await _audit.WriteAsync(new AuditEntry(action, AuditActorType.User, id, Partner?.Id, "user", id, IpAddress: ClientIp, UserAgent: ClientUserAgent), cancellationToken);
        }

        if (IsEndSessionRequest)
        {
            // Let OpenIddict validate post_logout_redirect_uri against the client's registration and redirect.
            return SignOut(
                new AuthenticationProperties { RedirectUri = "/login?signedout=true" },
                OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        }

        return RedirectToPage("/Account/Login", new { signedout = true });
    }

    private bool IsEndSessionRequest => HttpContext.GetOpenIddictServerRequest() is not null;

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        Guid? id = SangamAuthentication.UserId(base.User);
        User = id is null ? null : await _accounts.FindByIdAsync(id.Value, cancellationToken);
        if (User is not null)
        {
            Initials = ((User.FirstName.Length > 0 ? User.FirstName[..1] : "") + (User.LastName.Length > 0 ? User.LastName[..1] : "")).ToUpperInvariant();
        }

        OpenIddictRequest? request = HttpContext.GetOpenIddictServerRequest();
        if (request is not null)
        {
            Dictionary<string, string> fields = new(StringComparer.Ordinal);
            foreach ((string name, string? value) in new[] { ("id_token_hint", request.IdTokenHint), ("post_logout_redirect_uri", request.PostLogoutRedirectUri), ("client_id", request.ClientId), ("state", request.State) })
            {
                if (!string.IsNullOrEmpty(value))
                {
                    fields[name] = value;
                }
            }

            EndSessionFields = fields;
            if (request.ClientId is string clientId)
            {
                Partner = await _apps.FindByClientIdAsync(clientId, cancellationToken);
                PartnerReturnUrl = request.PostLogoutRedirectUri;
            }
        }
    }
}
