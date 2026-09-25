using System.Security.Claims;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using OpenIddict.Validation.AspNetCore;
using Sangam.Identity.Application.Abstractions;
using Sangam.Identity.Application.Accounts;
using Sangam.Identity.Application.Apps;
using Sangam.Identity.Application.Consents;
using Sangam.Identity.Application.Tenancy;
using Sangam.Identity.Domain;
using Sangam.Identity.Domain.Enums;
using Sangam.Identity.Server.Authentication;
using Sangam.Identity.Server.Authorization;
using Sangam.Shared.Constants;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Sangam.Identity.Server.Endpoints;

/// <summary>The OAuth 2 / OpenID Connect protocol endpoints under <c>/connect</c>.</summary>
public static class ConnectEndpoints
{
    /// <summary>Absolute cap on how long a refresh-token chain may live, regardless of sliding renewals.</summary>
    public static readonly TimeSpan AuthorizationMaxAge = TimeSpan.FromDays(90);

    /// <summary>Maps the authorization, token, userinfo and end-session endpoints.</summary>
    /// <param name="endpoints">Endpoint route builder.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static IEndpointRouteBuilder MapConnectEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapMethods("/connect/authorize", [HttpMethods.Get, HttpMethods.Post], AuthorizeAsync).WithName("Authorize").DisableAntiforgery();
        endpoints.MapPost("/connect/token", ExchangeAsync).WithName("Token").DisableAntiforgery();
        endpoints.MapMethods("/connect/userinfo", [HttpMethods.Get, HttpMethods.Post], UserInfoAsync).WithName("UserInfo")
            .RequireAuthorization(p => p.AddAuthenticationSchemes(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme).RequireAuthenticatedUser());
        return endpoints;
    }

    // ------------------------------------------------------------------ authorize

    private static async Task<IResult> AuthorizeAsync(
        HttpContext httpContext,
        IAccountService accounts,
        IAppDirectory apps,
        IConsentService consents,
        ITenancyQuery tenancy,
        IAuditWriter audit,
        IOpenIddictApplicationManager applications,
        IOpenIddictAuthorizationManager authorizations,
        IOpenIddictScopeManager scopes,
        CancellationToken cancellationToken)
    {
        OpenIddictRequest request = httpContext.GetOpenIddictServerRequest()
            ?? throw new InvalidOperationException("The OpenIddict server request cannot be retrieved.");

        AppSummary? app = request.ClientId is null ? null : await apps.FindByClientIdAsync(request.ClientId, cancellationToken).ConfigureAwait(false);
        if (app is null || app.Status != AppStatus.Active)
        {
            return Forbid(Errors.InvalidClient, "This application is not registered with Sangam or has been disabled.");
        }

        string returnUrl = httpContext.Request.PathBase + httpContext.Request.Path + httpContext.Request.QueryString;

        // 1. Signed in?  (prompt=login always re-authenticates)
        AuthenticateResult session = await httpContext.AuthenticateAsync(IdentityConstants.ApplicationScheme).ConfigureAwait(false);
        Guid? userId = session.Succeeded && session.Principal is not null ? SangamAuthentication.UserId(session.Principal) : null;
        UserSummary? user = userId is null ? null : await accounts.FindByIdAsync(userId.Value, cancellationToken).ConfigureAwait(false);

        if (user is null || request.HasPromptValue(PromptValues.Login))
        {
            if (request.HasPromptValue(PromptValues.None))
            {
                return Forbid(Errors.LoginRequired, "The user is not signed in.");
            }

            await httpContext.SignOutAsync(IdentityConstants.ApplicationScheme).ConfigureAwait(false);
            return Results.Redirect("/login?returnUrl=" + Uri.EscapeDataString(StripPrompt(returnUrl)));
        }

        // 2. Does the session satisfy the app's sign-in policy?
        SignInMode required = SignInModes.Resolve(app.SignInPolicy, user.SignInPreference);
        SignInMode sessionMode = SignInModes.TryParse(session.Principal!.FindFirstValue(SangamAuthentication.SessionModeClaim), out SignInMode m) ? m : SignInMode.Password;
        if (!PartnerContext.Satisfies(required, sessionMode))
        {
            if (request.HasPromptValue(PromptValues.None))
            {
                return Forbid(Errors.LoginRequired, "This application requires a stronger sign-in.");
            }

            await httpContext.SignOutAsync(IdentityConstants.ApplicationScheme).ConfigureAwait(false);
            return Results.Redirect("/login?returnUrl=" + Uri.EscapeDataString(returnUrl));
        }

        // 3. Consent — every app is third-party; a denial parked in the pending cookie ends the request.
        System.Collections.Immutable.ImmutableArray<string> requested = request.GetScopes();
        PendingFlow? denied = await SangamAuthentication.ReadPendingAsync(httpContext, SangamAuthentication.Pending.ConsentDenied).ConfigureAwait(false);
        if (denied is not null && string.Equals(denied.Email, app.ClientId, StringComparison.Ordinal))
        {
            await SangamAuthentication.ClearPendingAsync(httpContext).ConfigureAwait(false);
            return Forbid(Errors.AccessDenied, "The user declined to share their Sangam account with this application.");
        }

        bool consented = await consents.HasValidConsentAsync(user.Id, app.Id, requested, cancellationToken).ConfigureAwait(false);
        if (!consented)
        {
            if (request.HasPromptValue(PromptValues.None))
            {
                return Forbid(Errors.ConsentRequired, "The user has not consented to this application.");
            }

            return Results.Redirect("/consent?returnUrl=" + Uri.EscapeDataString(returnUrl));
        }

        // 4. Issue the code.
        object application = await applications.FindByClientIdAsync(app.ClientId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The OpenIddict client is missing.");
        string applicationId = (await applications.GetIdAsync(application, cancellationToken).ConfigureAwait(false))!;

        IReadOnlyList<OrgClaim> orgs = await tenancy.GetOrgClaimsAsync(user.Id, app.Id, cancellationToken).ConfigureAwait(false);
        // Standard OIDC `sid`: lets a client (the portal, for one) tell which session is its own.
        string? browserSession = SangamAuthentication.SessionId(session.Principal!)?.ToString("D");
        ClaimsIdentity identity = SangamClaimsBuilder.Build(user, requested, orgs, TokenValidationParameters.DefaultAuthenticationType, browserSession);

        List<string> resources = [];
        await foreach (string resource in scopes.ListResourcesAsync(identity.GetScopes(), cancellationToken).ConfigureAwait(false))
        {
            resources.Add(resource);
        }

        identity.SetResources(resources);

        // Reuse the permanent authorization for this user + app + scopes, so revoking it at the portal revokes every token.
        object? authorization = null;
        await foreach (object candidate in authorizations.FindAsync(user.Id.ToString("D"), applicationId, Statuses.Valid, AuthorizationTypes.Permanent, requested, cancellationToken).ConfigureAwait(false))
        {
            authorization = candidate;
            break;
        }

        authorization ??= await authorizations.CreateAsync(identity, user.Id.ToString("D"), applicationId, AuthorizationTypes.Permanent, requested, cancellationToken).ConfigureAwait(false);
        identity.SetAuthorizationId(await authorizations.GetIdAsync(authorization, cancellationToken).ConfigureAwait(false));

        await audit.WriteAsync(
            new AuditEntry(AuditActions.TokenIssue, AuditActorType.User, user.Id, app.Id, "app", app.Id, Metadata: "{\"grant\":\"authorization_code\"}",
                IpAddress: httpContext.Connection.RemoteIpAddress?.ToString()),
            cancellationToken).ConfigureAwait(false);

        return Results.SignIn(new ClaimsPrincipal(identity), authenticationScheme: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    // ------------------------------------------------------------------ token

    private static async Task<IResult> ExchangeAsync(
        HttpContext httpContext,
        IAccountService accounts,
        IAppDirectory apps,
        ITenancyQuery tenancy,
        IAuditWriter audit,
        IOpenIddictApplicationManager applications,
        IOpenIddictAuthorizationManager authorizations,
        IOpenIddictScopeManager scopes,
        CancellationToken cancellationToken)
    {
        OpenIddictRequest request = httpContext.GetOpenIddictServerRequest()
            ?? throw new InvalidOperationException("The OpenIddict server request cannot be retrieved.");

        if (request.IsClientCredentialsGrantType())
        {
            return await ClientCredentialsAsync(request, applications, scopes, cancellationToken).ConfigureAwait(false);
        }

        if (request.IsAuthorizationCodeGrantType() || request.IsRefreshTokenGrantType())
        {
            // The principal validated by OpenIddict from the code / refresh token.
            AuthenticateResult result = await httpContext.AuthenticateAsync(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme).ConfigureAwait(false);
            ClaimsPrincipal? stored = result.Principal;
            Guid? userId = stored is null ? null : (Guid.TryParse(stored.GetClaim(Claims.Subject), out Guid id) ? id : null);
            UserSummary? user = userId is null ? null : await accounts.FindByIdAsync(userId.Value, cancellationToken).ConfigureAwait(false);
            if (user is null || !user.EmailVerified)
            {
                return Forbid(Errors.InvalidGrant, "The user no longer exists or cannot sign in.");
            }

            AppSummary? app = request.ClientId is null ? null : await apps.FindByClientIdAsync(request.ClientId, cancellationToken).ConfigureAwait(false);
            if (app is null || app.Status != AppStatus.Active)
            {
                return Forbid(Errors.InvalidClient, "This application is not registered with Sangam or has been disabled.");
            }

            string? authorizationId = stored!.GetAuthorizationId();
            if (request.IsRefreshTokenGrantType() && authorizationId is not null)
            {
                object? authorization = await authorizations.FindByIdAsync(authorizationId, cancellationToken).ConfigureAwait(false);
                DateTimeOffset? created = authorization is null ? null : await authorizations.GetCreationDateAsync(authorization, cancellationToken).ConfigureAwait(false);
                if (created is null || DateTimeOffset.UtcNow - created.Value > AuthorizationMaxAge)
                {
                    return Forbid(Errors.InvalidGrant, "This sign-in is older than 90 days; the user must sign in again.");
                }
            }

            // Re-read the user so profile and membership changes reach the new tokens.
            IReadOnlyList<OrgClaim> orgs = await tenancy.GetOrgClaimsAsync(user.Id, app.Id, cancellationToken).ConfigureAwait(false);
            ClaimsPrincipal principal = stored!;
            ClaimsIdentity identity = SangamClaimsBuilder.Build(user, [.. principal.GetScopes()], orgs, TokenValidationParameters.DefaultAuthenticationType, principal.GetClaim(SangamClaims.SessionId));
            identity.SetAuthorizationId(authorizationId);

            List<string> resources = [];
            await foreach (string resource in scopes.ListResourcesAsync(identity.GetScopes(), cancellationToken).ConfigureAwait(false))
            {
                resources.Add(resource);
            }

            identity.SetResources(resources);

            await audit.WriteAsync(
                new AuditEntry(AuditActions.TokenIssue, AuditActorType.User, user.Id, app.Id, "app", app.Id,
                    Metadata: $"{{\"grant\":\"{(request.IsRefreshTokenGrantType() ? "refresh_token" : "authorization_code")}\"}}",
                    IpAddress: httpContext.Connection.RemoteIpAddress?.ToString()),
                cancellationToken).ConfigureAwait(false);

            return Results.SignIn(new ClaimsPrincipal(identity), authenticationScheme: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        }

        return Forbid(Errors.UnsupportedGrantType, "The specified grant type is not supported.");
    }

    private static async Task<IResult> ClientCredentialsAsync(OpenIddictRequest request, IOpenIddictApplicationManager applications, IOpenIddictScopeManager scopes, CancellationToken cancellationToken)
    {
        object application = await applications.FindByClientIdAsync(request.ClientId!, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The application details cannot be found in the database.");

        ClaimsIdentity identity = new(TokenValidationParameters.DefaultAuthenticationType, Claims.Name, Claims.Role);
        identity.SetClaim(Claims.Subject, await applications.GetClientIdAsync(application, cancellationToken).ConfigureAwait(false));
        identity.SetClaim(Claims.Name, await applications.GetDisplayNameAsync(application, cancellationToken).ConfigureAwait(false));
        identity.SetScopes(request.GetScopes());

        List<string> resources = [];
        await foreach (string resource in scopes.ListResourcesAsync(identity.GetScopes(), cancellationToken).ConfigureAwait(false))
        {
            resources.Add(resource);
        }

        identity.SetResources(resources);
        identity.SetDestinations(static claim => claim.Type switch
        {
            Claims.Name or Claims.Subject => [Destinations.AccessToken, Destinations.IdentityToken],
            _ => [Destinations.AccessToken],
        });

        return Results.SignIn(new ClaimsPrincipal(identity), authenticationScheme: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    // ------------------------------------------------------------------ userinfo

    private static async Task<IResult> UserInfoAsync(HttpContext httpContext, IAccountService accounts, IAppDirectory apps, ITenancyQuery tenancy, CancellationToken cancellationToken)
    {
        ClaimsPrincipal principal = httpContext.User;
        Guid? userId = Guid.TryParse(principal.GetClaim(Claims.Subject), out Guid id) ? id : null;
        UserSummary? user = userId is null ? null : await accounts.FindByIdAsync(userId.Value, cancellationToken).ConfigureAwait(false);
        if (user is null)
        {
            return Results.Challenge(
                authenticationSchemes: [OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme],
                properties: new AuthenticationProperties(new Dictionary<string, string?>
                {
                    [OpenIddictValidationAspNetCoreConstants.Properties.Error] = Errors.InvalidToken,
                    [OpenIddictValidationAspNetCoreConstants.Properties.ErrorDescription] = "The user no longer exists.",
                }));
        }

        HashSet<string> granted = [.. principal.GetScopes()];
        Dictionary<string, object> claims = new(StringComparer.Ordinal) { [Claims.Subject] = user.Id.ToString("D") };

        if (granted.Contains(SangamScopes.Profile))
        {
            claims[Claims.Name] = user.DisplayName;
            claims[Claims.GivenName] = user.FirstName;
            claims[Claims.FamilyName] = user.LastName;
            claims[Claims.Birthdate] = user.DateOfBirth.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            claims[Claims.Gender] = Genders.ToCode(user.Gender);
            claims[Claims.Locale] = user.Locale;
            claims[Claims.Zoneinfo] = "Asia/Kolkata";
            claims[Claims.UpdatedAt] = user.UpdatedAt.ToUnixTimeSeconds();
        }

        if (granted.Contains(SangamScopes.Email))
        {
            claims[Claims.Email] = user.Email;
            claims[Claims.EmailVerified] = user.EmailVerified;
        }

        if (granted.Contains(SangamScopes.Phone) && !string.IsNullOrEmpty(user.Mobile))
        {
            claims[Claims.PhoneNumber] = user.Mobile;
            claims[Claims.PhoneNumberVerified] = user.MobileVerified;
        }

        if (granted.Contains(SangamScopes.OrgsRead))
        {
            string? clientId = principal.GetClaim(Claims.Audience) ?? principal.GetClaim(Claims.ClientId);
            AppSummary? app = clientId is null ? null : await apps.FindByClientIdAsync(clientId, cancellationToken).ConfigureAwait(false);
            IReadOnlyList<OrgClaim> orgs = app is null ? [] : await tenancy.GetOrgClaimsAsync(user.Id, app.Id, cancellationToken).ConfigureAwait(false);
            claims[SangamClaims.Orgs] = orgs.Select(o => new Dictionary<string, object>
            {
                [SangamOrgClaim.Id] = o.Id.ToString("D"),
                [SangamOrgClaim.Name] = o.Name,
                [SangamOrgClaim.Type] = o.Type,
                [SangamOrgClaim.Path] = o.Path,
                [SangamOrgClaim.Role] = o.Role,
                [SangamOrgClaim.Permissions] = o.Permissions,
                [SangamOrgClaim.Inherits] = o.Inherits,
            }).ToArray();
        }

        return Results.Ok(claims);
    }

    // ------------------------------------------------------------------ helpers

    private static IResult Forbid(string error, string description) => Results.Forbid(
        authenticationSchemes: [OpenIddictServerAspNetCoreDefaults.AuthenticationScheme],
        properties: new AuthenticationProperties(new Dictionary<string, string?>
        {
            [OpenIddictServerAspNetCoreConstants.Properties.Error] = error,
            [OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] = description,
        }));

    /// <summary>Removes <c>prompt=login</c> from the return URL so the round-trip after sign-in does not loop.</summary>
    private static string StripPrompt(string url)
    {
        int q = url.IndexOf('?', StringComparison.Ordinal);
        if (q < 0)
        {
            return url;
        }

        IEnumerable<string> kept = url[(q + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries).Where(p => !p.StartsWith("prompt=", StringComparison.OrdinalIgnoreCase));
        return url[..q] + "?" + string.Join('&', kept);
    }
}
