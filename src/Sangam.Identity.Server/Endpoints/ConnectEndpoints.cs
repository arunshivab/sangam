using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using OpenIddict.Validation.AspNetCore;
using Sangam.Identity.Application.Abstractions;
using Sangam.Identity.Application.Accounts;
using Sangam.Identity.Application.Apps;
using Sangam.Identity.Application.Consents;
using Sangam.Identity.Application.Devices;
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
        IDataProtectionProvider protection,
        IOrgChoiceStore orgChoices,
        ISharedDeviceService devices,
        IEmergencyAccessService emergencies,
        Sangam.Identity.Application.Passkeys.IPasskeyService passkeys,
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

        // 1b. rc.12 (T1): a session an application locked waits for the person's PIN (on the shared ward device it was locked
        //     on, within the customer's limits) or a full sign-in; on an enrolled device, a person without a PIN is offered
        //     one once per sign-in.
        Guid? sessionRow = SangamAuthentication.SessionId(session.Principal!);
        if (sessionRow is not null)
        {
            SharedDeviceInfo? device = await SharedDeviceCookie.CurrentAsync(httpContext, cancellationToken).ConfigureAwait(false);
            DeviceGate gate = await devices.GateAsync(sessionRow.Value, device?.Id, cancellationToken).ConfigureAwait(false);
            if (gate is DeviceGate.Unlock or DeviceGate.SignInAgain && request.HasPromptValue(PromptValues.None))
            {
                return Forbid(Errors.LoginRequired, "The session is locked.");
            }

            if (gate == DeviceGate.Unlock)
            {
                return Results.Redirect("/unlock?returnUrl=" + Uri.EscapeDataString(StripPrompt(returnUrl)));
            }

            if (gate == DeviceGate.SignInAgain)
            {
                await sessions.EndAsync(sessionRow.Value, "locked", cancellationToken).ConfigureAwait(false);
                await httpContext.SignOutAsync(IdentityConstants.ApplicationScheme).ConfigureAwait(false);
                return Results.Redirect("/login?returnUrl=" + Uri.EscapeDataString(StripPrompt(returnUrl)));
            }

            if (gate == DeviceGate.OfferPin && !request.HasPromptValue(PromptValues.None))
            {
                return Results.Redirect("/shared-device/pin?returnUrl=" + Uri.EscapeDataString(returnUrl));
            }
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

        // 2d. rc.6 (SGM-914 section 4): the application requires its people to be verified with DigiLocker.
        if (app.RequireIdentityVerification && user.IdentityVerifiedAt is null)
        {
            await audit.WriteAsync(new AuditEntry(AuditActions.IdentityVerifyRequired, AuditActorType.User, user.Id, app.Id, "app", app.Id), cancellationToken).ConfigureAwait(false);
            if (request.HasPromptValue(PromptValues.None))
            {
                return Forbid(Errors.InteractionRequired, "This application requires the person to verify their identity with DigiLocker.");
            }

            return Results.Redirect("/identity/required?returnUrl=" + Uri.EscapeDataString(returnUrl));
        }

        // 2e. rc.12 (A6): "Working at which branch?" — asked of a person with two or more organisations in the application,
        //     unless the application named one they work at, or they chose on this device. The token carries the one chosen.
        System.Collections.Immutable.ImmutableArray<string> asked = request.GetScopes();
        OrgClaim? current = null;
        IReadOnlyList<OrgClaim> orgs = await tenancy.GetOrgClaimsAsync(user.Id, app.Id, cancellationToken).ConfigureAwait(false);
        if (asked.Contains(SangamScopes.OrgsRead) || asked.Contains(SangamScopes.OrgCurrent))
        {
            Guid? hint = Guid.TryParse(request.GetParameter(PartnerContext.OrganisationParameter)?.ToString(), out Guid named) ? named : null;
            current = OrgChoice.Resolve(orgs, hint, OrgChoice.Read(httpContext, protection, user.Id, app.Id), request.HasPromptValue(OrgChoice.SelectOrgPrompt));
            if (current is null && orgs.Count > 1)
            {
                if (request.HasPromptValue(PromptValues.None))
                {
                    return Forbid(Errors.InteractionRequired, "The person must choose which organisation they are working at.");
                }

                return Results.Redirect("/select-org?returnUrl=" + Uri.EscapeDataString(returnUrl));
            }

            if (current is not null && orgs.Count > 1)
            {
                await orgChoices.RememberAsync(user.Id, app.Id, current.Id, cancellationToken).ConfigureAwait(false);
            }
        }

        // 2f. rc.12 (M12): emergency access ("break-glass"), when the application asks for it with its own case reference. The
        //     person proves it is them again (a passkey or an authenticator app, within five minutes), gives a reason on
        //     Sangam's screen, and the token carries the emergency marker until it ends.
        EmergencyGrant? emergency = null;
        string? caseRef = request.GetParameter(SangamClaims.Emergency)?.ToString();
        if (!string.IsNullOrEmpty(caseRef))
        {
            if (request.HasPromptValue(PromptValues.None))
            {
                return Forbid(Errors.InteractionRequired, "Emergency access needs the person on Sangam's screen.");
            }

            emergency = await emergencies.ActiveAsync(user.Id, app.Id, sessionRow, caseRef, cancellationToken).ConfigureAwait(false);
            if (emergency is null)
            {
                OrgClaim? workingAt = current
                    ?? OrgChoice.Resolve(orgs, Guid.TryParse(request.GetParameter(PartnerContext.OrganisationParameter)?.ToString(), out Guid hinted) ? hinted : null, OrgChoice.Read(httpContext, protection, user.Id, app.Id), false)
                    ?? (orgs.Count == 1 ? orgs[0] : null);
                if (workingAt is null && orgs.Count > 1)
                {
                    return Results.Redirect("/select-org?returnUrl=" + Uri.EscapeDataString(returnUrl));
                }

                bool fresh = SangamAuthentication.HasSecondFactor(session.Principal!)
                    && SangamAuthentication.AuthenticatedAt(session.Principal!) is DateTimeOffset signedIn
                    && DateTimeOffset.UtcNow - signedIn <= EmergencyFreshness;
                bool canProve = user.MfaEnrolled || (passkeys.Enabled && (await passkeys.ListAsync(user.Id, cancellationToken).ConfigureAwait(false)).Count > 0);
                if (!fresh && canProve)
                {
                    await httpContext.SignOutAsync(IdentityConstants.ApplicationScheme).ConfigureAwait(false);
                    return Results.Redirect("/login?returnUrl=" + Uri.EscapeDataString(WithTwoFactors(StripPrompt(returnUrl))));
                }

                return Results.Redirect("/emergency?returnUrl=" + Uri.EscapeDataString(returnUrl) + (workingAt is null ? string.Empty : "&org=" + workingAt.Id.ToString("D")));
            }
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

        // Standard OIDC `sid`: lets a client (the portal, for one) tell which session is its own.
        string? browserSession = SangamAuthentication.SessionId(session.Principal!)?.ToString("D");
        ClaimsIdentity identity = SangamClaimsBuilder.Build(user, requested, orgs, TokenValidationParameters.DefaultAuthenticationType, browserSession, proof, await CustomClaimsAsync(httpContext, requested, user.Id, app.Id, cancellationToken).ConfigureAwait(false), current, emergency);
        if (SangamAuthentication.SessionId(session.Principal!) is Guid sid)
        {
            // PR-20: this application now takes part in the session and is told when it ends.
            await sessions.RecordAppAsync(sid, app.Id, user.Id, cancellationToken).ConfigureAwait(false);
        }

        // rc.6 (SGM-910 section 7): signing in to the application keeps the connection from ending for inactivity.
        await sessions.RecordUseAsync(user.Id, app.Id, cancellationToken).ConfigureAwait(false);

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
        ISessionService sessions,
        IEmergencyAccessService emergencies,
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
            if (user is null || !user.EmailVerified || user.Status != UserStatus.Active)
            {
                return Forbid(Errors.InvalidGrant, "The user no longer exists or cannot sign in.");
            }

            // R7 (ASVS V3.3.1, V3.3.3): a refresh token dies with the sign-in it came from. Signing out (here or
            // everywhere), an operator ending the session, a password reset or a change to two-step sign-in all end it;
            // before, refresh tokens went on working for up to 90 days.
            if (request.IsRefreshTokenGrantType())
            {
                string? stamp = stored!.GetClaim(SangamClaims.SecurityStamp);
                if (stamp is not null && !string.Equals(stamp, user.SecurityStamp, StringComparison.Ordinal))
                {
                    return Forbid(Errors.InvalidGrant, "The person's password, e-mail or two-step sign-in changed, or they signed out everywhere; they must sign in again.");
                }

                if (Guid.TryParse(stored!.GetClaim(SangamClaims.SessionId), out Guid sessionId)
                    && !await sessions.TouchAsync(sessionId, user.Id, cancellationToken).ConfigureAwait(false))
                {
                    return Forbid(Errors.InvalidGrant, "The sign-in this refresh token came from has ended; the user must sign in again.");
                }
            }

            AppSummary? app = request.ClientId is null ? null : await apps.FindByClientIdAsync(request.ClientId, cancellationToken).ConfigureAwait(false);
            if (app is null || app.Status != AppStatus.Active)
            {
                return Forbid(Errors.InvalidClient, "This application is not registered with Sangam or has been disabled.");
            }

            // rc.6 (SGM-910 section 7): a code redeemed or tokens refreshed count as using the application.
            await sessions.RecordUseAsync(user.Id, app.Id, cancellationToken).ConfigureAwait(false);

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

            // rc.12 (A6): the organisation chosen at sign-in carries over to every token from it, while the person still works there.
            Guid? chosenOrg = OrgIdOf(principal.GetClaim(SangamClaims.Org));
            OrgClaim? current = orgs.FirstOrDefault(o => o.Id == chosenOrg) ?? (orgs.Count == 1 ? orgs[0] : null);

            // rc.12 (M12): emergency access carries over only while it lasts; once ended (early or at 60 minutes) it is gone.
            EmergencyGrant? emergency = SangamClaimsBuilder.EmergencyIdOf(principal.GetClaim(SangamClaims.Emergency)) is Guid useId
                ? await emergencies.ActiveByIdAsync(useId, user.Id, cancellationToken).ConfigureAwait(false)
                : null;
            ClaimsIdentity identity = SangamClaimsBuilder.Build(user, [.. principal.GetScopes()], orgs, TokenValidationParameters.DefaultAuthenticationType, principal.GetClaim(SangamClaims.SessionId), AuthenticationProof.FromToken(principal), await CustomClaimsAsync(httpContext, principal.GetScopes(), user.Id, app.Id, cancellationToken).ConfigureAwait(false), current, emergency);
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

    /// <summary>rc.12 (M12): how recently the person must have proved it is them (passkey or authenticator) for emergency access.</summary>
    private static readonly TimeSpan EmergencyFreshness = TimeSpan.FromMinutes(5);

    /// <summary>rc.12 (M12): the return address asking for two factors, so the sign-in page asks for the authenticator or a passkey.</summary>
    private static string WithTwoFactors(string returnUrl)
        => returnUrl.Contains("acr_values=", StringComparison.Ordinal) ? returnUrl : returnUrl + (returnUrl.Contains('?', StringComparison.Ordinal) ? "&" : "?") + "acr_values=" + Uri.EscapeDataString(AuthenticationAssurance.Acr2);

    /// <summary>The organisation id inside a <c>sangam_org</c> claim, if it is one.</summary>
    private static Guid? OrgIdOf(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty(SangamOrgClaim.Id, out JsonElement id)
                && Guid.TryParse(id.GetString(), out Guid org) ? org : null;
        }
        catch (JsonException)
        {
            return null;
        }
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
        ClaimsIdentity identity = SangamClaimsBuilder.Build(user, granted, orgs, TokenValidationParameters.DefaultAuthenticationType, subject.GetClaim(SangamClaims.SessionId), AuthenticationProof.FromToken(subject), await CustomClaimsAsync(httpContext, granted, user.Id, target.Id, cancellationToken).ConfigureAwait(false));
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

    private static async Task<IResult> UserInfoAsync(HttpContext httpContext, IAccountService accounts, IAppDirectory apps, ITenancyQuery tenancy, IEmergencyAccessService emergencies, CancellationToken cancellationToken)
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

        if (granted.Contains(SangamScopes.OrgsRead) || granted.Contains(SangamScopes.OrgCurrent))
        {
            string? clientId = principal.GetClaim(Claims.Audience) ?? principal.GetClaim(Claims.ClientId);
            AppSummary? app = clientId is null ? null : await apps.FindByClientIdAsync(clientId, cancellationToken).ConfigureAwait(false);
            IReadOnlyList<OrgClaim> orgs = app is null ? [] : await tenancy.GetOrgClaimsAsync(user.Id, app.Id, cancellationToken).ConfigureAwait(false);
            if (granted.Contains(SangamScopes.OrgsRead))
            {
                claims[SangamClaims.Orgs] = orgs.Select(SangamClaimsBuilder.ToJson).ToArray();
            }

            // rc.13: the organisation chosen at sign-in, as in the token, while the person still works there.
            Guid? chosen = OrgIdOf(principal.GetClaim(SangamClaims.Org));
            if (orgs.FirstOrDefault(o => o.Id == chosen) is OrgClaim current)
            {
                claims[SangamClaims.Org] = SangamClaimsBuilder.ToJson(current);
            }
        }

        // rc.13: emergency access in progress, as in the token, only while it lasts.
        if (SangamClaimsBuilder.EmergencyIdOf(principal.GetClaim(SangamClaims.Emergency)) is Guid useId
            && await emergencies.ActiveByIdAsync(useId, user.Id, cancellationToken).ConfigureAwait(false) is EmergencyGrant emergency)
        {
            claims[SangamClaims.Emergency] = SangamClaimsBuilder.EmergencyJson(emergency);
        }

        if (granted.Contains(SangamScopes.Attributes))
        {
            string? clientId = principal.GetClaim(Claims.Audience) ?? principal.GetClaim(Claims.ClientId);
            AppSummary? app = clientId is null ? null : await apps.FindByClientIdAsync(clientId, cancellationToken).ConfigureAwait(false);
            if (app is not null)
            {
                foreach ((string name, object value) in await CustomClaimsAsync(httpContext, granted, user.Id, app.Id, cancellationToken).ConfigureAwait(false) ?? new Dictionary<string, object>())
                {
                    claims.TryAdd(name, value);
                }
            }
        }

        return Results.Ok(claims);
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>PR-25: the application's custom claims for the person, when the <c>attributes</c> scope is granted.</summary>
    private static async Task<IReadOnlyDictionary<string, object>?> CustomClaimsAsync(HttpContext httpContext, IEnumerable<string> scopes, Guid userId, Guid appId, CancellationToken cancellationToken)
        => scopes.Contains(SangamScopes.Attributes, StringComparer.Ordinal)
            ? await httpContext.RequestServices.GetRequiredService<Sangam.Identity.Application.Attributes.IAttributeService>().ClaimsAsync(userId, appId, cancellationToken).ConfigureAwait(false)
            : null;

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
