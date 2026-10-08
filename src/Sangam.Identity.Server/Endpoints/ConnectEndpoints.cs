using System.Security.Claims;
using System.Text.Json;
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
using Sangam.Identity.Application.Portal;
using Sangam.Identity.Application.Security;
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
        endpoints.MapPost("/connect/token", ExchangeAsync).WithName("Token").DisableAntiforgery().RequireRateLimiting(AuthRateLimiting.TokenPolicy);
        endpoints.MapMethods("/connect/userinfo", [HttpMethods.Get, HttpMethods.Post], UserInfoAsync).WithName("UserInfo").RequireRateLimiting(AuthRateLimiting.UserInfoPolicy)
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
        ISecurityPolicyService policies,
        ISessionService sessions,
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
        if (!string.IsNullOrEmpty(request.RequestUri))
        {
            // PR-21, a pushed request (RFC 9126): its parameters are not in the address. The sign-in and consent pages read
            // the scopes, level and language from the return address, so copy them there; OpenIddict ignores them and
            // keeps using the pushed request.
            returnUrl = httpContext.Request.PathBase + httpContext.Request.Path
                + "?client_id=" + Uri.EscapeDataString(request.ClientId ?? string.Empty)
                + "&request_uri=" + Uri.EscapeDataString(request.RequestUri)
                + (string.IsNullOrEmpty(request.Scope) ? string.Empty : "&scope=" + Uri.EscapeDataString(request.Scope))
                + (string.IsNullOrEmpty(request.AcrValues) ? string.Empty : "&acr_values=" + Uri.EscapeDataString(request.AcrValues))
                + (string.IsNullOrEmpty(request.UiLocales) ? string.Empty : "&ui_locales=" + Uri.EscapeDataString(request.UiLocales));
        }

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

        // 2. Does the session satisfy the sign-in policy — the application's, tightened by the person's organisations (PR-16)?
        PersonPolicy person = await policies.ForPersonAsync(user.Id, app.Id, cancellationToken).ConfigureAwait(false);
        SignInMode required = SignInModes.Resolve(person.Policy.SignIn, user.SignInPreference);
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

        // 2b. A second factor, where the policy requires one: an authenticator step in this session, or a passkey (PR-16).
        if (person.RequiresSecondFactor && !SangamAuthentication.HasSecondFactor(session.Principal!))
        {
            if (request.HasPromptValue(PromptValues.None))
            {
                return Forbid(Errors.LoginRequired, "This application requires two-step sign-in.");
            }

            if (user.MfaEnrolled)
            {
                // Enrolled, but this session predates it: signing in again asks for the authenticator.
                await httpContext.SignOutAsync(IdentityConstants.ApplicationScheme).ConfigureAwait(false);
                return Results.Redirect("/login?returnUrl=" + Uri.EscapeDataString(returnUrl));
            }

            return Results.Redirect("/login/two-step-required?returnUrl=" + Uri.EscapeDataString(returnUrl));
        }

        // 2c. Step-up (PR-17, SGM-207): the level the application asked for with acr_values, and how recent the
        //     authentication must be (max_age; a signature-grade request is never older than five minutes).
        (int requiredLevel, bool signature) = AuthenticationAssurance.Required(request.GetAcrValues());
        long? maxAge = request.MaxAge;
        if (signature)
        {
            maxAge = Math.Min(maxAge ?? long.MaxValue, (long)AuthenticationAssurance.SignatureFreshness.TotalSeconds);
        }

        AuthenticationProof proof = AuthenticationProof.FromSession(session.Principal!);
        bool tooWeak = AuthenticationAssurance.Level(proof.Acr) < requiredLevel;
        bool tooOld = maxAge is long limit && (proof.AuthenticatedAt is not DateTimeOffset at || DateTimeOffset.UtcNow - at > TimeSpan.FromSeconds(limit));
        if (tooWeak || tooOld)
        {
            await audit.WriteAsync(
                new AuditEntry(AuditActions.UserStepUpRequired, AuditActorType.User, user.Id, app.Id, "app", app.Id,
                    Metadata: $"{{\"reason\":\"{(tooWeak ? "level" : "age")}\",\"had\":\"{proof.Acr}\",\"required_level\":{requiredLevel}}}"),
                cancellationToken).ConfigureAwait(false);
            if (request.HasPromptValue(PromptValues.None))
            {
                return Forbid(Errors.LoginRequired, "This application requires a stronger or more recent sign-in.");
            }

            // Re-authenticate even though a session exists; the sign-in page reads the level from the return URL.
            await httpContext.SignOutAsync(IdentityConstants.ApplicationScheme).ConfigureAwait(false);
            return Results.Redirect("/login?returnUrl=" + Uri.EscapeDataString(StripPrompt(returnUrl)));
        }

        if (requiredLevel > 0 || maxAge is not null)
        {
            await audit.WriteAsync(
                new AuditEntry(AuditActions.UserStepUpSuccess, AuditActorType.User, user.Id, app.Id, "app", app.Id,
                    Metadata: $"{{\"acr\":\"{proof.Acr}\",\"required_level\":{requiredLevel}}}"),
                cancellationToken).ConfigureAwait(false);
        }

        // 3. Consent — partner applications ask; Sangam's own (portal, consoles) are first-party and
        //    consent is implicit, but still recorded (V-06). A denial parked in the pending cookie ends the request.
        System.Collections.Immutable.ImmutableArray<string> requested = request.GetScopes();
        PendingFlow? denied = await SangamAuthentication.ReadPendingAsync(httpContext, SangamAuthentication.Pending.ConsentDenied).ConfigureAwait(false);
        if (denied is not null && string.Equals(denied.Email, app.ClientId, StringComparison.Ordinal))
        {
            await SangamAuthentication.ClearPendingAsync(httpContext).ConfigureAwait(false);
            return Forbid(Errors.AccessDenied, "The user declined to share their Sangam account with this application.");
        }

        bool consented = await consents.HasValidConsentAsync(user.Id, app.Id, requested, cancellationToken).ConfigureAwait(false);
        if (!consented && app.IsPlatform)
        {
            string? ip = httpContext.Connection.RemoteIpAddress?.ToString();
            string userAgent = httpContext.Request.Headers.UserAgent.ToString();
            await consents.GrantAsync(user.Id, app.Id, requested, ip, userAgent, firstPartyImplicit: true, cancellationToken: cancellationToken).ConfigureAwait(false);
            consented = true;
        }

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
        ClaimsIdentity identity = SangamClaimsBuilder.Build(user, requested, orgs, TokenValidationParameters.DefaultAuthenticationType, browserSession, proof);
        if (SangamAuthentication.SessionId(session.Principal!) is Guid sid)
        {
            // PR-20: this application now takes part in the session and is told when it ends.
            await sessions.RecordAppAsync(sid, app.Id, user.Id, cancellationToken).ConfigureAwait(false);
        }

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
        IConsentService consents,
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

        if (request.IsTokenExchangeGrantType())
        {
            return await TokenExchangeAsync(httpContext, request, accounts, apps, tenancy, consents, audit, cancellationToken).ConfigureAwait(false);
        }

        // PR-21: the device code grant redeems what the person approved at /device exactly like a code.
        if (request.IsAuthorizationCodeGrantType() || request.IsRefreshTokenGrantType() || request.IsDeviceCodeGrantType())
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
            ClaimsIdentity identity = SangamClaimsBuilder.Build(user, [.. principal.GetScopes()], orgs, TokenValidationParameters.DefaultAuthenticationType, principal.GetClaim(SangamClaims.SessionId), AuthenticationProof.FromToken(principal));
            identity.SetAuthorizationId(authorizationId);

            List<string> resources = [];
            await foreach (string resource in scopes.ListResourcesAsync(identity.GetScopes(), cancellationToken).ConfigureAwait(false))
            {
                resources.Add(resource);
            }

            identity.SetResources(resources);

            await audit.WriteAsync(
                new AuditEntry(AuditActions.TokenIssue, AuditActorType.User, user.Id, app.Id, "app", app.Id,
                    Metadata: $"{{\"grant\":\"{(request.IsRefreshTokenGrantType() ? "refresh_token" : request.IsDeviceCodeGrantType() ? "device_code" : "authorization_code")}\"}}",
                    IpAddress: httpContext.Connection.RemoteIpAddress?.ToString()),
                cancellationToken).ConfigureAwait(false);

            return Results.SignIn(new ClaimsPrincipal(identity), authenticationScheme: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        }

        return Forbid(Errors.UnsupportedGrantType, "The specified grant type is not supported.");
    }

    /// <summary>How long an exchanged token lives: long enough for one call chain, never refreshable.</summary>
    public static readonly TimeSpan ExchangedTokenLifetime = TimeSpan.FromMinutes(10);

    /// <summary>
    /// PR-21, token exchange (RFC 8693): a confidential application's back end trades the access token a person's
    /// sign-in gave it for a short-lived token addressed to another Sangam application (one audience), so that one
    /// service can call the next on that person's behalf — LiPi HIS reading from the laboratory system, say. Only
    /// with the caller's audience permission for the target (<c>aud:&lt;client id&gt;</c>, checked by OpenIddict), only a
    /// token the caller itself was issued, only where the person has consented to the target application, never
    /// more scopes than they gave, never a refresh token; the caller is named in <c>act</c>, and it is audited.
    /// </summary>
    private static async Task<IResult> TokenExchangeAsync(
        HttpContext httpContext,
        OpenIddictRequest request,
        IAccountService accounts,
        IAppDirectory apps,
        ITenancyQuery tenancy,
        IConsentService consents,
        IAuditWriter audit,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(request.SubjectTokenType, TokenTypeIdentifiers.AccessToken, StringComparison.Ordinal))
        {
            return Forbid(Errors.InvalidRequest, "Only an access token can be exchanged.");
        }

        System.Collections.Immutable.ImmutableArray<string> audiences = request.GetAudiences();
        if (audiences.Length != 1)
        {
            return Forbid(Errors.InvalidTarget, "Name exactly one audience: the client id of the application the token is for.");
        }

        AppSummary? caller = request.ClientId is null ? null : await apps.FindByClientIdAsync(request.ClientId, cancellationToken).ConfigureAwait(false);
        AppSummary? target = await apps.FindByClientIdAsync(audiences[0], cancellationToken).ConfigureAwait(false);
        if (caller is null || caller.Status != AppStatus.Active || target is null || target.Status != AppStatus.Active || target.Id == caller.Id)
        {
            return Forbid(Errors.InvalidTarget, "The audience is not an active Sangam application other than the caller.");
        }

        // The subject token, validated by OpenIddict (signature, lifetime, not revoked).
        AuthenticateResult result = await httpContext.AuthenticateAsync(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme).ConfigureAwait(false);
        ClaimsPrincipal? subject = result.Principal;
        if (subject is null || !subject.GetPresenters().Contains(caller.ClientId))
        {
            return Forbid(Errors.InvalidGrant, "Only a token issued to the calling application can be exchanged.");
        }

        Guid? userId = Guid.TryParse(subject.GetClaim(Claims.Subject), out Guid id) ? id : null;
        UserSummary? user = userId is null ? null : await accounts.FindByIdAsync(userId.Value, cancellationToken).ConfigureAwait(false);
        if (user is null || !user.EmailVerified)
        {
            return Forbid(Errors.InvalidGrant, "The user no longer exists or cannot sign in.");
        }

        System.Collections.Immutable.ImmutableArray<string> held = subject.GetScopes();
        List<string> granted = [.. (request.GetScopes().IsDefaultOrEmpty ? held : request.GetScopes()).Where(s => held.Contains(s) && s != Scopes.OfflineAccess)];
        if (request.GetScopes().Any(s => !held.Contains(s)))
        {
            return Forbid(Errors.InvalidScope, "An exchanged token cannot carry more than the original.");
        }

        if (!await consents.HasValidConsentAsync(user.Id, target.Id, granted, cancellationToken).ConfigureAwait(false))
        {
            return Forbid(Errors.InvalidGrant, "The person has not allowed the target application to see this.");
        }

        IReadOnlyList<OrgClaim> orgs = await tenancy.GetOrgClaimsAsync(user.Id, target.Id, cancellationToken).ConfigureAwait(false);
        ClaimsIdentity identity = SangamClaimsBuilder.Build(user, granted, orgs, TokenValidationParameters.DefaultAuthenticationType, subject.GetClaim(SangamClaims.SessionId), AuthenticationProof.FromToken(subject));
        identity.SetAudiences(target.ClientId);
        identity.SetResources(target.ClientId);
        identity.SetAccessTokenLifetime(ExchangedTokenLifetime);
        // RFC 8693 §4.1: who is acting for the person.
        identity.AddClaim(new Claim("act", JsonSerializer.Serialize(new Dictionary<string, string> { [Claims.Subject] = caller.ClientId }), "JSON"));
        foreach (Claim claim in identity.Claims.Where(c => c.Type == "act"))
        {
            claim.SetDestinations(Destinations.AccessToken);
        }

        await audit.WriteAsync(
            new AuditEntry(AuditActions.TokenExchange, AuditActorType.Api, user.Id, caller.Id, "app", target.Id,
                Metadata: JsonSerializer.Serialize(new Dictionary<string, object> { ["from"] = caller.ClientId, ["to"] = target.ClientId, ["scopes"] = granted }),
                IpAddress: httpContext.Connection.RemoteIpAddress?.ToString()),
            cancellationToken).ConfigureAwait(false);

        return Results.SignIn(new ClaimsPrincipal(identity), authenticationScheme: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
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

    /// <summary>
    /// Removes <c>prompt</c> and <c>max_age</c> from the return URL: once the person has just authenticated, the
    /// round-trip must not ask again and loop (<c>max_age=0</c> would otherwise never be met). <c>acr_values</c>
    /// stays, so the sign-in page knows the level to reach (PR-17).
    /// </summary>
    private static string StripPrompt(string url)
    {
        int q = url.IndexOf('?', StringComparison.Ordinal);
        if (q < 0)
        {
            return url;
        }

        IEnumerable<string> kept = url[(q + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(p => !p.StartsWith("prompt=", StringComparison.OrdinalIgnoreCase) && !p.StartsWith("max_age=", StringComparison.OrdinalIgnoreCase));
        return url[..q] + "?" + string.Join('&', kept);
    }
}
