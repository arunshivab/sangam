using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using OpenIddict.Abstractions;
using OpenIddict.Validation.AspNetCore;
using Sangam.Identity.Application.Abstractions;
using Sangam.Identity.Application.Accounts;
using Sangam.Identity.Application.Apps;
using Sangam.Identity.Application.Attributes;
using Sangam.Identity.Application.Signatures;
using Sangam.Identity.Application.Tenancy;
using Sangam.Identity.Domain;
using Sangam.Identity.Domain.Enums;
using Sangam.Identity.Server.Authentication;
using Sangam.Shared.Constants;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Sangam.Identity.Server.Api;

/// <summary>
/// The management API partner apps call with their client credentials
/// (<c>scope=sangam.manage</c>). Every route is scoped to the calling app: the app id comes
/// from the token, never from the URL, so an app cannot address another app's data.
/// </summary>
public static class ManagementEndpoints
{
    /// <summary>Authorization policy name: a valid access token carrying <c>sangam.manage</c>.</summary>
    public const string PolicyName = "sangam.manage";

    /// <summary>Registers the policy.</summary>
    /// <param name="options">Authorization options.</param>
    public static void AddManagementPolicy(this AuthorizationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.AddPolicy(PolicyName, p => p
            .AddAuthenticationSchemes(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme)
            .RequireAuthenticatedUser()
            .RequireAssertion(ctx => ctx.User.HasScope(SangamScopes.Manage)));
    }

    /// <summary>Maps <c>/api/v1/...</c>.</summary>
    /// <param name="endpoints">Endpoint route builder.</param>
    /// <returns>The same builder.</returns>
    public static IEndpointRouteBuilder MapManagementEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        RouteGroupBuilder api = endpoints.MapGroup("/api/v1").RequireAuthorization(PolicyName).DisableAntiforgery().RequireRateLimiting(AuthRateLimiting.ApiPolicy)
            .AddEndpointFilter(AccessDeniedAudit.FilterAsync)
            .AddEndpointFilter(JsonOnlyFilter.FilterAsync);

        api.MapGet("/roles", async (ClaimsPrincipal caller, IAppDirectory apps, IManagementService mgmt, CancellationToken ct) =>
        {
            AppSummary? app = await CallerAppAsync(caller, apps, ct).ConfigureAwait(false);
            return app is null ? Results.Forbid() : Results.Ok(await mgmt.ListRolesAsync(app.Id, ct).ConfigureAwait(false));
        }).WithName("ListRoles");

        api.MapPut("/roles/{code}", async (string code, RoleUpsert input, ClaimsPrincipal caller, IAppDirectory apps, IManagementService mgmt, CancellationToken ct) =>
        {
            AppSummary? app = await CallerAppAsync(caller, apps, ct).ConfigureAwait(false);
            return app is null ? Results.Forbid() : ToResult(await mgmt.UpsertRoleAsync(app.Id, code, input, ManagementActor.Api, ct).ConfigureAwait(false));
        }).WithName("UpsertRole");

        api.MapDelete("/roles/{code}", async (string code, ClaimsPrincipal caller, IAppDirectory apps, IManagementService mgmt, CancellationToken ct) =>
        {
            AppSummary? app = await CallerAppAsync(caller, apps, ct).ConfigureAwait(false);
            return app is null ? Results.Forbid() : ToResult(await mgmt.RetireRoleAsync(app.Id, code, ManagementActor.Api, ct).ConfigureAwait(false));
        }).WithName("RetireRole");

        api.MapGet("/orgs/{orgId:guid}", async (Guid orgId, ClaimsPrincipal caller, IAppDirectory apps, IManagementService mgmt, CancellationToken ct) =>
        {
            AppSummary? app = await CallerAppAsync(caller, apps, ct).ConfigureAwait(false);
            if (app is null)
            {
                return Results.Forbid();
            }

            OrganisationDto? org = await mgmt.GetOrganisationAsync(app.Id, orgId, ct).ConfigureAwait(false);
            return org is null ? Results.NotFound() : Results.Ok(org);
        }).WithName("GetOrganisation");

        api.MapPut("/orgs/{orgId:guid}", async (Guid orgId, OrganisationUpsert input, ClaimsPrincipal caller, IAppDirectory apps, IManagementService mgmt, CancellationToken ct) =>
        {
            AppSummary? app = await CallerAppAsync(caller, apps, ct).ConfigureAwait(false);
            return app is null ? Results.Forbid() : ToResult(await mgmt.UpsertOrganisationAsync(app.Id, orgId, input, ManagementActor.Api, ct).ConfigureAwait(false));
        }).WithName("UpsertOrganisation");

        api.MapGet("/orgs/{orgId:guid}/members", async (Guid orgId, ClaimsPrincipal caller, IAppDirectory apps, IManagementService mgmt, IAuditWriter audit, CancellationToken ct) =>
        {
            AppSummary? app = await CallerAppAsync(caller, apps, ct).ConfigureAwait(false);
            if (app is null)
            {
                return Results.Forbid();
            }

            IReadOnlyList<MembershipDto> members = await mgmt.ListMembersAsync(app.Id, orgId, ct).ConfigureAwait(false);

            // rc.8 (ASVS V8.3.5): an application reading a list of people is recorded, as a console administrator's read is.
            await audit.WriteAsync(
                new AuditEntry(
                    AuditActions.AppPeopleView,
                    AuditActorType.Api,
                    ActorAppId: app.Id,
                    TargetType: "organisation",
                    TargetId: orgId,
                    Metadata: JsonSerializer.Serialize(new Dictionary<string, object> { ["list"] = "org_members", ["rows"] = members.Count, ["searched"] = false })),
                ct).ConfigureAwait(false);
            return Results.Ok(members);
        }).WithName("ListMembers");

        api.MapPut("/orgs/{orgId:guid}/members/{userId:guid}", async (Guid orgId, Guid userId, MembershipUpsert input, ClaimsPrincipal caller, IAppDirectory apps, IManagementService mgmt, CancellationToken ct) =>
        {
            AppSummary? app = await CallerAppAsync(caller, apps, ct).ConfigureAwait(false);
            return app is null ? Results.Forbid() : ToResult(await mgmt.UpsertMembershipAsync(app.Id, orgId, userId, input, ManagementActor.Api, ct).ConfigureAwait(false));
        }).WithName("UpsertMembership");

        // rc.12: invitations by e-mail alone are replaced by adding staff under an admin's own identity (below).
        api.MapPost("/orgs/{orgId:guid}/invitations", (Guid orgId) => Results.Json(
            new { error = "Invitations by e-mail were replaced in 1.0.0-rc.12. Add the person with POST /api/v1/orgs/" + orgId.ToString("D") + "/staff, sending the admin's own access token in the " + ActingAdminHeader + " header." },
            statusCode: StatusCodes.Status410Gone)).WithName("InviteMember");

        // rc.12 (founder decisions, 14 Oct 2026): adding staff. The application acts for one of its admins, who signed in to
        // it with Sangam: their access token, issued to this application, goes in the Sangam-Acting-Admin header, and every
        // action is recorded under their name. Only a DigiLocker-verified admin may add (or link) people.
        api.MapPost("/orgs/{orgId:guid}/staff/check", async (Guid orgId, StaffDetailsRequest input, HttpContext http, ClaimsPrincipal caller, IAppDirectory apps, IStaffService staff, OpenIddict.Validation.OpenIddictValidationService validation, CancellationToken ct) =>
        {
            ArgumentNullException.ThrowIfNull(input);
            AppSummary? app = await CallerAppAsync(caller, apps, ct).ConfigureAwait(false);
            Guid? admin = app is null ? null : await ActingAdminAsync(http, app, validation, ct).ConfigureAwait(false);
            if (app is null || admin is null)
            {
                return app is null ? Results.Forbid() : MissingAdmin();
            }

            StaffCheckResult? check = await staff.CheckAsync(new StaffActor(app.Id, admin.Value, ViaApi: true), orgId, input.ToDetails(), ct).ConfigureAwait(false);
            return check is null ? Results.NotFound(new { error = "The organisation does not exist, or that person is not an admin there." }) : Results.Ok(CheckJson(check));
        }).WithName("CheckStaff");

        api.MapPost("/orgs/{orgId:guid}/staff", async (Guid orgId, StaffAddRequest input, HttpContext http, ClaimsPrincipal caller, IAppDirectory apps, IStaffService staff, OpenIddict.Validation.OpenIddictValidationService validation, CancellationToken ct) =>
        {
            ArgumentNullException.ThrowIfNull(input);
            AppSummary? app = await CallerAppAsync(caller, apps, ct).ConfigureAwait(false);
            Guid? admin = app is null ? null : await ActingAdminAsync(http, app, validation, ct).ConfigureAwait(false);
            if (app is null || admin is null)
            {
                return app is null ? Results.Forbid() : MissingAdmin();
            }

            StaffAddResult result = await staff.AddAsync(new StaffActor(app.Id, admin.Value, ViaApi: true), orgId, input.ToAddition(), ct).ConfigureAwait(false);
            object body = new
            {
                status = Snake(result.Status.ToString()),
                message = result.Message,
                staffRef = result.StaffRef,
                userId = result.UserId,
                activationCode = result.ActivationCode,
                check = result.Check is null ? null : CheckJson(result.Check),
                addedBeforeDigiLocker = result.AddedBeforeDigiLocker,
            };
            return result.Status switch
            {
                StaffAddStatus.Added or StaffAddStatus.Waiting or StaffAddStatus.NeedsConfirmation => Results.Ok(body),
                StaffAddStatus.NotFound => Results.NotFound(body),
                StaffAddStatus.TooMany => Results.Json(body, statusCode: StatusCodes.Status429TooManyRequests),
                _ => Results.BadRequest(body),
            };
        }).WithName("AddStaff");

        api.MapGet("/orgs/{orgId:guid}/staff/waiting", async (Guid orgId, HttpContext http, ClaimsPrincipal caller, IAppDirectory apps, IStaffService staff, OpenIddict.Validation.OpenIddictValidationService validation, CancellationToken ct) =>
        {
            AppSummary? app = await CallerAppAsync(caller, apps, ct).ConfigureAwait(false);
            Guid? admin = app is null ? null : await ActingAdminAsync(http, app, validation, ct).ConfigureAwait(false);
            if (app is null || admin is null)
            {
                return app is null ? Results.Forbid() : MissingAdmin();
            }

            IReadOnlyList<StaffWaiting>? waiting = await staff.WaitingAsync(new StaffActor(app.Id, admin.Value, ViaApi: true), orgId, ct).ConfigureAwait(false);
            return waiting is null ? Results.NotFound(new { error = "The organisation does not exist, or that person is not an admin there." }) : Results.Ok(waiting);
        }).WithName("ListWaitingStaff");

        api.MapDelete("/staff/waiting/{waitingId:guid}", async (Guid waitingId, HttpContext http, ClaimsPrincipal caller, IAppDirectory apps, IStaffService staff, OpenIddict.Validation.OpenIddictValidationService validation, CancellationToken ct) =>
        {
            AppSummary? app = await CallerAppAsync(caller, apps, ct).ConfigureAwait(false);
            Guid? admin = app is null ? null : await ActingAdminAsync(http, app, validation, ct).ConfigureAwait(false);
            if (app is null || admin is null)
            {
                return app is null ? Results.Forbid() : MissingAdmin();
            }

            return await staff.CancelAsync(new StaffActor(app.Id, admin.Value, ViaApi: true), waitingId, ct).ConfigureAwait(false)
                ? Results.NoContent()
                : Results.NotFound(new { error = "No waiting addition with that id that this admin may cancel." });
        }).WithName("CancelWaitingStaff");

        api.MapDelete("/orgs/{orgId:guid}/members/{userId:guid}", async (Guid orgId, Guid userId, ClaimsPrincipal caller, IAppDirectory apps, IManagementService mgmt, CancellationToken ct) =>
        {
            AppSummary? app = await CallerAppAsync(caller, apps, ct).ConfigureAwait(false);
            return app is null ? Results.Forbid() : ToResult(await mgmt.RevokeMembershipAsync(app.Id, orgId, userId, ManagementActor.Api, ct).ConfigureAwait(false));
        }).WithName("RevokeMembership");

        // PR-25: custom attribute values, for people who have linked the calling application.
        api.MapGet("/users/{userId:guid}/attributes", async (Guid userId, ClaimsPrincipal caller, IAppDirectory apps, IAttributeService attributes, CancellationToken ct) =>
        {
            AppSummary? app = await CallerAppAsync(caller, apps, ct).ConfigureAwait(false);
            if (app is null)
            {
                return Results.Forbid();
            }

            IReadOnlyList<AttributeValueRow>? rows = await attributes.GetValuesAsync(null, app.Id, userId, ct).ConfigureAwait(false);
            return rows is null ? Results.NotFound() : Results.Ok(rows.ToDictionary(r => r.Key, r => r.Value));
        }).WithName("GetUserAttributes");

        // rc.7 (SGM-914 section 9): where a closed account went, for an application that knew it. 404 for any id the
        // application never had, merged or not, so the API tells it nothing about other people.
        api.MapGet("/users/{userId:guid}/merged", async (Guid userId, ClaimsPrincipal caller, IAppDirectory apps, IAccountMergeService merges, CancellationToken ct) =>
        {
            AppSummary? app = await CallerAppAsync(caller, apps, ct).ConfigureAwait(false);
            if (app is null)
            {
                return Results.Forbid();
            }

            MergedInto? merged = await merges.MergedIntoAsync(app.Id, userId, ct).ConfigureAwait(false);
            return merged is null
                ? Results.NotFound()
                : Results.Ok(new { merged_into = merged.UserId.ToString("D"), merged_at = merged.MergedAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture) });
        }).WithName("GetMergedInto");

        api.MapPut("/users/{userId:guid}/attributes", async (Guid userId, Dictionary<string, string?> values, ClaimsPrincipal caller, IAppDirectory apps, IAttributeService attributes, CancellationToken ct) =>
        {
            AppSummary? app = await CallerAppAsync(caller, apps, ct).ConfigureAwait(false);
            if (app is null)
            {
                return Results.Forbid();
            }

            Sangam.Identity.Application.Partners.PartnerResult result = await attributes.SetValuesAsync(null, app.Id, userId, values ?? [], ct).ConfigureAwait(false);
            return result.Succeeded ? Results.Ok(new { message = result.Message }) : Results.Problem(title: result.Message, statusCode: StatusCodes.Status400BadRequest);
        }).WithName("SetUserAttributes");

        // PR-17 (SGM-207 §5.1): electronic-signature requests. The person signs in Sangam's ceremony at /sign/{id}.
        api.MapPost("/signatures", async (SignatureRequestInput input, ClaimsPrincipal caller, IAppDirectory apps, IOpenIddictApplicationManager clients, ISignatureService signatures, CancellationToken ct) =>
        {
            AppSummary? app = await CallerAppAsync(caller, apps, ct).ConfigureAwait(false);
            if (app is null)
            {
                return Results.Forbid();
            }

            // The person is sent back only to an address the application registered, never one the request invents.
            object? client = await clients.FindByClientIdAsync(app.ClientId, ct).ConfigureAwait(false);
            System.Collections.Immutable.ImmutableArray<string> redirects = client is null ? [] : await clients.GetRedirectUrisAsync(client, ct).ConfigureAwait(false);
            if (input?.ReturnUrl is null || !redirects.Contains(input.ReturnUrl, StringComparer.Ordinal))
            {
                return Results.Problem(title: "returnUrl must be one of the application's registered redirect URIs.", statusCode: StatusCodes.Status400BadRequest);
            }

            (SignatureRequestCreated? created, string? error) = await signatures.CreateAsync(app.Id, input, ct).ConfigureAwait(false);
            return created is null
                ? Results.Problem(title: error, statusCode: StatusCodes.Status400BadRequest)
                : Results.Created("/api/v1/signatures/" + created.RequestId.ToString("D"), created);
        }).WithName("CreateSignatureRequest");

        api.MapGet("/signatures/{requestId:guid}", async (Guid requestId, ClaimsPrincipal caller, IAppDirectory apps, ISignatureService signatures, CancellationToken ct) =>
        {
            AppSummary? app = await CallerAppAsync(caller, apps, ct).ConfigureAwait(false);
            if (app is null)
            {
                return Results.Forbid();
            }

            SignatureView? view = await signatures.GetForAppAsync(app.Id, requestId, ct).ConfigureAwait(false);
            return view is null ? Results.NotFound() : Results.Ok(view);
        }).WithName("GetSignatureRequest");

        return endpoints;
    }

    /// <summary>rc.12: the header carrying the acting admin's own access token.</summary>
    public const string ActingAdminHeader = "Sangam-Acting-Admin";

    /// <summary>
    /// The admin the application acts for: their access token (from signing in to this application with Sangam) is valid,
    /// was issued to this same application, and names a person. Anything else is no admin.
    /// </summary>
    private static async Task<Guid?> ActingAdminAsync(HttpContext http, AppSummary app, OpenIddict.Validation.OpenIddictValidationService validation, CancellationToken ct)
    {
        string? token = http.Request.Headers[ActingAdminHeader].ToString();
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        if (token.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            token = token["Bearer ".Length..].Trim();
        }

        try
        {
            ClaimsPrincipal admin = await validation.ValidateAccessTokenAsync(token, ct).ConfigureAwait(false);
            string? clientId = admin.GetClaim(Claims.ClientId) ?? admin.GetClaim(Claims.Private.Presenter);
            bool sameApp = string.Equals(clientId, app.ClientId, StringComparison.Ordinal) || admin.GetPresenters().Contains(app.ClientId, StringComparer.Ordinal);
            return sameApp && Guid.TryParse(admin.GetClaim(Claims.Subject), out Guid id) ? id : null;
        }
        catch (Exception ex) when (ex is OpenIddict.Abstractions.OpenIddictExceptions.ValidationException or OpenIddict.Abstractions.OpenIddictExceptions.ProtocolException or InvalidOperationException)
        {
            return null;
        }
    }

    private static IResult MissingAdmin() => Results.Json(
        new { error = "Adding staff is done for one of your admins: send their own Sangam access token, issued to your application, in the " + ActingAdminHeader + " header." },
        statusCode: StatusCodes.Status401Unauthorized);

    private static object CheckJson(StaffCheckResult check) => new
    {
        outcome = Snake(check.Outcome.ToString()),
        matching = check.Matching,
        differing = check.Differing,
        message = check.Message,
        checksLeftToday = check.ChecksLeftToday,
    };

    private static string Snake(string name) => string.Concat(name.Select((c, i) => i > 0 && char.IsUpper(c) ? "_" + char.ToLowerInvariant(c) : char.ToLowerInvariant(c).ToString()));

    private static async Task<AppSummary?> CallerAppAsync(ClaimsPrincipal caller, IAppDirectory apps, CancellationToken ct)
    {
        // Client-credentials tokens carry the client id as the subject.
        string? clientId = caller.GetClaim(Claims.Subject);
        if (clientId is null)
        {
            return null;
        }

        AppSummary? app = await apps.FindByClientIdAsync(clientId, ct).ConfigureAwait(false);
        return app is { Status: AppStatus.Active } ? app : null;
    }

    private static IResult ToResult<T>(ManagementResult<T> result) => result.Status switch
    {
        ManagementStatus.Ok => Results.Ok(result.Value),
        ManagementStatus.NotFound => Results.NotFound(new { error = result.Message }),
        _ => Results.BadRequest(new { error = result.Message }),
    };
}
