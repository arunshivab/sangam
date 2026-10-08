using System.Security.Claims;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using OpenIddict.Abstractions;
using OpenIddict.Server;
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
/// <para>
/// PR-20: applications used in the session with a front-channel logout page are loaded in hidden frames
/// (with <c>iss</c> and <c>sid</c>) on a "signed out" page before moving on; for an app-initiated sign-out that
/// page re-posts the end-session request with <c>frames_done</c> so OpenIddict can then redirect to the app.
/// Back-channel notifications are queued by <see cref="ISessionService.EndAsync"/> and sent in the background.
/// </para>
/// </summary>
public sealed class LogoutModel : AuthPageModel
{
    private readonly IAccountService _accounts;
    private readonly IAppDirectory _apps;
    private readonly IAuditWriter _audit;
    private readonly ISessionService _sessions;
    private readonly IOptionsMonitor<OpenIddictServerOptions> _server;

    /// <summary>Initialises the page.</summary>
    public LogoutModel(IAccountService accounts, IAppDirectory apps, IAuditWriter audit, ISessionService sessions, IOptionsMonitor<OpenIddictServerOptions> server)
    {
        _server = server ?? throw new ArgumentNullException(nameof(server));
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

    /// <summary>Set once the session has ended and front-channel pages are being told (PR-20).</summary>
    public bool SignedOut { get; private set; }

    /// <summary>The hidden frames to load: each application's front-channel page with <c>iss</c> and <c>sid</c>.</summary>
    public IReadOnlyList<FrontChannelLogout> Frames { get; private set; } = [];

    /// <summary>Where the "signed out" page continues to once the frames have loaded (plain sign-out).</summary>
    public string ContinueUrl { get; private set; } = "/login?signedout=true";

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
        IReadOnlyList<FrontChannelLogout> frames = [];
        if (sessionId is not null)
        {
            // Read before ending: the applications that took part in this session and want to be told in the browser.
            frames = await _sessions.FrontChannelLogoutsAsync(sessionId.Value, cancellationToken);
            await _sessions.EndAsync(sessionId.Value, "user", cancellationToken);
        }

        await HttpContext.SignOutAsync(IdentityConstants.ApplicationScheme);
        await SangamAuthentication.ClearPendingAsync(HttpContext);

        if (id is not null)
        {
            string action = Partner is null ? AuditActions.UserLogout : AuditActions.UserLogoutApp;
            await _audit.WriteAsync(new AuditEntry(action, AuditActorType.User, id, Partner?.Id, "user", id, IpAddress: ClientIp, UserAgent: ClientUserAgent), cancellationToken);
        }

        if (frames.Count > 0 && sessionId is Guid sid)
        {
            // Render as signed out, so the page's own antiforgery token belongs to nobody (the next post is anonymous).
            HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity());
            User = null;
            SignedOut = true;
            string issuer = Issuer();
            Frames = [.. frames.Select(f => f with { Uri = WithLogoutParameters(f.Uri, issuer, sid) })];
            if (IsEndSessionRequest)
            {
                Dictionary<string, string> fields = new(EndSessionFields, StringComparer.Ordinal) { ["frames_done"] = "1" };
                EndSessionFields = fields;
            }

            return Page();
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

    /// <summary>Appends <c>iss</c> and <c>sid</c> to a front-channel logout address (Front-Channel Logout 1.0, §2).</summary>
    /// <param name="uri">The registered address.</param>
    /// <param name="issuer">Sangam's issuer.</param>
    /// <param name="sessionId">The ended session.</param>
    internal static string WithLogoutParameters(string uri, string issuer, Guid sessionId)
    {
        ArgumentNullException.ThrowIfNull(uri);
        string separator = uri.Contains('?', StringComparison.Ordinal) ? "&" : "?";
        return uri + separator + "iss=" + Uri.EscapeDataString(issuer) + "&sid=" + Uri.EscapeDataString(sessionId.ToString("D"));
    }

    // The same issuer OpenIddict puts in tokens: the configured one, otherwise this request's own address.
    private string Issuer()
        => _server.CurrentValue.Issuer?.AbsoluteUri ?? $"{Request.Scheme}://{Request.Host}{Request.PathBase}/";

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
