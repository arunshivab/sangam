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

        api.MapGet("/orgs/{orgId:guid}/members", async (Guid orgId, ClaimsPrincipal caller, IAppDirectory apps, ISubjectIds subjects, IManagementService mgmt, IAuditWriter audit, CancellationToken ct) =>
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
            return Results.Ok(await ForAppAsync(members, app.Id, subjects, ct).ConfigureAwait(false));
        }).WithName("ListMembers");

        // rc.13 (decision of 14 Oct 2026): changing a person's place says which admin asked, and that admin must be one.
        api.MapPut("/orgs/{orgId:guid}/members/{userId:guid}", async (Guid orgId, Guid userId, MembershipUpsert input, HttpContext http, ClaimsPrincipal caller, IAppDirectory apps, ISubjectIds subjects, IManagementService mgmt, IStaffService staff, OpenIddict.Validation.OpenIddictValidationService validation, CancellationToken ct) =>
        {
            AppSummary? app = await CallerAppAsync(caller, apps, ct).ConfigureAwait(false);
            Guid? admin = app is null ? null : await ActingAdminAsync(http, app, subjects, validation, ct).ConfigureAwait(false);
            if (app is null || admin is null)
            {
                return app is null ? Results.Forbid() : MissingAdmin();
            }

            // rc.14: the person is named by this application's own id; one it does not know is not found.
            if (await subjects.UserAsync(app.Id, userId, ct).ConfigureAwait(false) is not Guid person)
            {
                return UnknownPerson();
            }

            return await staff.ActingAdminAsync(new StaffActor(app.Id, admin.Value, ViaApi: true), orgId, person, ct).ConfigureAwait(false) switch
            {
                StaffActingAdmin.NotAdmin => NotAnAdminThere(),
                StaffActingAdmin.MustBeVerified => Results.Json(new { error = "Only an admin verified with DigiLocker can add people." }, statusCode: StatusCodes.Status403Forbidden),
                _ => ToResult(ForApp(await mgmt.UpsertMembershipAsync(app.Id, orgId, person, input, ManagementActor.ApiFor(admin.Value), ct).ConfigureAwait(false), userId)),
            };
        }).WithName("UpsertMembership");

        // rc.12: invitations by e-mail alone are replaced by adding staff under an admin's own identity (below).
        api.MapPost("/orgs/{orgId:guid}/invitations", (Guid orgId) => Results.Json(
            new { error = "Invitations by e-mail were replaced in 1.0.0-rc.12. Add the person with POST /api/v1/orgs/" + orgId.ToString("D") + "/staff, sending the admin's own access token in the " + ActingAdminHeader + " header." },
            statusCode: StatusCodes.Status410Gone)).WithName("InviteMember");

        // rc.12 (founder decisions, 14 Oct 2026): adding staff. The application acts for one of its admins, who signed in to
        // it with Sangam: their access token, issued to this application, goes in the Sangam-Acting-Admin header, and every
        // action is recorded under their name. Only a DigiLocker-verified admin may add (or link) people.
        api.MapPost("/orgs/{orgId:guid}/staff/check", async (Guid orgId, StaffDetailsRequest input, HttpContext http, ClaimsPrincipal caller, IAppDirectory apps, ISubjectIds subjects, IStaffService staff, OpenIddict.Validation.OpenIddictValidationService validation, CancellationToken ct) =>
        {
            ArgumentNullException.ThrowIfNull(input);
            AppSummary? app = await CallerAppAsync(caller, apps, ct).ConfigureAwait(false);
            Guid? admin = app is null ? null : await ActingAdminAsync(http, app, subjects, validation, ct).ConfigureAwait(false);
            if (app is null || admin is null)
            {
                return app is null ? Results.Forbid() : MissingAdmin();
            }

            StaffCheckResult? check = await staff.CheckAsync(new StaffActor(app.Id, admin.Value, ViaApi: true), orgId, input.ToDetails(), ct).ConfigureAwait(false);
            return check is null ? Results.NotFound(new { error = "The organisation does not exist, or that person is not an admin there." }) : Results.Ok(CheckJson(check));
        }).WithName("CheckStaff");

        api.MapPost("/orgs/{orgId:guid}/staff", async (Guid orgId, StaffAddRequest input, HttpContext http, ClaimsPrincipal caller, IAppDirectory apps, ISubjectIds subjects, IStaffService staff, OpenIddict.Validation.OpenIddictValidationService validation, CancellationToken ct) =>
        {
            ArgumentNullException.ThrowIfNull(input);
            AppSummary? app = await CallerAppAsync(caller, apps, ct).ConfigureAwait(false);
            Guid? admin = app is null ? null : await ActingAdminAsync(http, app, subjects, validation, ct).ConfigureAwait(false);
            if (app is null || admin is null)
            {
                return app is null ? Results.Forbid() : MissingAdmin();
            }

            // rc.14: someone already in the customer is named by this application's own id for them.
            StaffAddition addition = input.ToAddition();
            if (addition.UserId is Guid named)
            {
                if (await subjects.UserAsync(app.Id, named, ct).ConfigureAwait(false) is not Guid person)
                {
                    return UnknownPerson();
                }

                addition = addition with { UserId = person };
            }

            StaffAddResult result = await staff.AddAsync(new StaffActor(app.Id, admin.Value, ViaApi: true), orgId, addition, ct).ConfigureAwait(false);
            object body = new
            {
                status = Snake(result.Status.ToString()),
                message = result.Message,
                staffRef = result.StaffRef,
                userId = result.UserId is Guid joined ? await subjects.SubjectAsync(app.Id, joined, ct).ConfigureAwait(false) : (Guid?)null,
                activationCode = result.ActivationCode,
                check = result.Check is null ? null : CheckJson(result.Check),
                addedBeforeDigiLocker = result.AddedBeforeDigiLocker,
                waitsForDigiLocker = result.WaitsForDigiLocker,
            };
            return result.Status switch
            {
                StaffAddStatus.Added or StaffAddStatus.Waiting or StaffAddStatus.NeedsConfirmation => Results.Ok(body),
                StaffAddStatus.NotFound => Results.NotFound(body),
                StaffAddStatus.TooMany => Results.Json(body, statusCode: StatusCodes.Status429TooManyRequests),
                _ => Results.BadRequest(body),
            };
        }).WithName("AddStaff");

        api.MapGet("/orgs/{orgId:guid}/staff/waiting", async (Guid orgId, HttpContext http, ClaimsPrincipal caller, IAppDirectory apps, ISubjectIds subjects, IStaffService staff, OpenIddict.Validation.OpenIddictValidationService validation, CancellationToken ct) =>
        {
            AppSummary? app = await CallerAppAsync(caller, apps, ct).ConfigureAwait(false);
            Guid? admin = app is null ? null : await ActingAdminAsync(http, app, subjects, validation, ct).ConfigureAwait(false);
            if (app is null || admin is null)
            {
                return app is null ? Results.Forbid() : MissingAdmin();
            }

            IReadOnlyList<StaffWaiting>? waiting = await staff.WaitingAsync(new StaffActor(app.Id, admin.Value, ViaApi: true), orgId, ct).ConfigureAwait(false);
            return waiting is null ? Results.NotFound(new { error = "The organisation does not exist, or that person is not an admin there." }) : Results.Ok(waiting);
        }).WithName("ListWaitingStaff");

        api.MapDelete("/staff/waiting/{waitingId:guid}", async (Guid waitingId, HttpContext http, ClaimsPrincipal caller, IAppDirectory apps, ISubjectIds subjects, IStaffService staff, OpenIddict.Validation.OpenIddictValidationService validation, CancellationToken ct) =>
        {
            AppSummary? app = await CallerAppAsync(caller, apps, ct).ConfigureAwait(false);
            Guid? admin = app is null ? null : await ActingAdminAsync(http, app, subjects, validation, ct).ConfigureAwait(false);
            if (app is null || admin is null)
            {
                return app is null ? Results.Forbid() : MissingAdmin();
            }

            return await staff.CancelAsync(new StaffActor(app.Id, admin.Value, ViaApi: true), waitingId, ct).ConfigureAwait(false)
                ? Results.NoContent()
                : Results.NotFound(new { error = "No waiting addition with that id that this admin may cancel." });
        }).WithName("CancelWaitingStaff");

        api.MapDelete("/orgs/{orgId:guid}/members/{userId:guid}", async (Guid orgId, Guid userId, HttpContext http, ClaimsPrincipal caller, IAppDirectory apps, ISubjectIds subjects, IManagementService mgmt, IStaffService staff, OpenIddict.Validation.OpenIddictValidationService validation, CancellationToken ct) =>
        {
            AppSummary? app = await CallerAppAsync(caller, apps, ct).ConfigureAwait(false);
            Guid? admin = app is null ? null : await ActingAdminAsync(http, app, subjects, validation, ct).ConfigureAwait(false);
            if (app is null || admin is null)
            {
                return app is null ? Results.Forbid() : MissingAdmin();
            }

            if (await subjects.UserAsync(app.Id, userId, ct).ConfigureAwait(false) is not Guid person)
            {
                return UnknownPerson();
            }

            return await staff.ActingAdminAsync(new StaffActor(app.Id, admin.Value, ViaApi: true), orgId, person, ct).ConfigureAwait(false) == StaffActingAdmin.NotAdmin
                ? NotAnAdminThere()
                : ToResult(ForApp(await mgmt.RevokeMembershipAsync(app.Id, orgId, person, ManagementActor.ApiFor(admin.Value), ct).ConfigureAwait(false), userId));
        }).WithName("RevokeMembership");

        // PR-25: custom attribute values, for people who have linked the calling application.
        api.MapGet("/users/{userId:guid}/attributes", async (Guid userId, ClaimsPrincipal caller, IAppDirectory apps, ISubjectIds subjects, IAttributeService attributes, CancellationToken ct) =>
        {
            AppSummary? app = await CallerAppAsync(caller, apps, ct).ConfigureAwait(false);
            if (app is null)
            {
                return Results.Forbid();
            }

            if (await subjects.UserAsync(app.Id, userId, ct).ConfigureAwait(false) is not Guid person)
            {
                return Results.NotFound();
            }

            IReadOnlyList<AttributeValueRow>? rows = await attributes.GetValuesAsync(null, app.Id, person, ct).ConfigureAwait(false);
            return rows is null ? Results.NotFound() : Results.Ok(rows.ToDictionary(r => r.Key, r => r.Value));
        }).WithName("GetUserAttributes");

        // rc.7 (SGM-914 section 9): where a closed account went, for an application that knew it. 404 for any id the
        // application never had, merged or not, so the API tells it nothing about other people.
        api.MapGet("/users/{userId:guid}/merged", async (Guid userId, ClaimsPrincipal caller, IAppDirectory apps, ISubjectIds subjects, IAccountMergeService merges, CancellationToken ct) =>
        {
            AppSummary? app = await CallerAppAsync(caller, apps, ct).ConfigureAwait(false);
            if (app is null)
            {
                return Results.Forbid();
            }

            // rc.14: both ids are this application's own: the closed account's, and the survivor's (given out now if needed).
            MergedInto? merged = await subjects.UserAsync(app.Id, userId, ct).ConfigureAwait(false) is Guid closed
                ? await merges.MergedIntoAsync(app.Id, closed, ct).ConfigureAwait(false)
                : null;
            return merged is null
                ? Results.NotFound()
                : Results.Ok(new { merged_into = (await subjects.SubjectAsync(app.Id, merged.UserId, ct).ConfigureAwait(false)).ToString("D"), merged_at = merged.MergedAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture) });
        }).WithName("GetMergedInto");

        // rc.14 (decision of 14 Oct 2026): changing values about a person says which admin asked, and that admin must
        // administer the application or an organisation where the person works in it.
        api.MapPut("/users/{userId:guid}/attributes", async (Guid userId, Dictionary<string, string?> values, HttpContext http, ClaimsPrincipal caller, IAppDirectory apps, ISubjectIds subjects, IAttributeService attributes, OpenIddict.Validation.OpenIddictValidationService validation, CancellationToken ct) =>
        {
            AppSummary? app = await CallerAppAsync(caller, apps, ct).ConfigureAwait(false);
            Guid? admin = app is null ? null : await ActingAdminAsync(http, app, subjects, validation, ct).ConfigureAwait(false);
            if (app is null || admin is null)
            {
                return app is null ? Results.Forbid() : MissingAdmin();
            }

            if (await subjects.UserAsync(app.Id, userId, ct).ConfigureAwait(false) is not Guid person)
            {
                return UnknownPerson();
            }

            (AttributeSetOutcome outcome, Sangam.Identity.Application.Partners.PartnerResult result) = await attributes.SetValuesForAdminAsync(app.Id, admin.Value, person, values ?? [], ct).ConfigureAwait(false);
            if (outcome == AttributeSetOutcome.NotAdmin)
            {
                return Results.NotFound(new { error = "That person does not work anywhere this admin administers in your application." });
            }

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
    private static async Task<Guid?> ActingAdminAsync(HttpContext http, AppSummary app, ISubjectIds subjects, OpenIddict.Validation.OpenIddictValidationService validation, CancellationToken ct)
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
            // rc.14: the token names the admin by this application's own id, and the admin's account must still be active.
            Guid? person = sameApp && Guid.TryParse(admin.GetClaim(Claims.Subject), out Guid id) ? await subjects.UserAsync(app.Id, id, ct).ConfigureAwait(false) : null;
            UserSummary? account = person is Guid found ? await http.RequestServices.GetRequiredService<IAccountService>().FindByIdAsync(found, ct).ConfigureAwait(false) : null;
            return account is { Status: UserStatus.Active, EmailVerified: true } ? account.Id : null;
        }
        catch (Exception ex) when (ex is OpenIddict.Abstractions.OpenIddictExceptions.ValidationException or OpenIddict.Abstractions.OpenIddictExceptions.ProtocolException or InvalidOperationException)
        {
            return null;
        }
    }

    private static IResult MissingAdmin() => Results.Json(
        new { error = "Changing people is done for one of your admins: send their own Sangam access token, issued to your application, in the " + ActingAdminHeader + " header." },
        statusCode: StatusCodes.Status401Unauthorized);

    private static IResult UnknownPerson() => Results.NotFound(new { error = "No person with that id has used your application." });

    /// <summary>rc.14: a membership as the application sees it, under the id it sent.</summary>
    private static ManagementResult<MembershipDto> ForApp(ManagementResult<MembershipDto> result, Guid subject)
        => result.Value is null ? result : result with { Value = result.Value with { UserId = subject } };

    /// <summary>rc.14: memberships as the application sees them, under its own ids for the people.</summary>
    private static async Task<IReadOnlyList<MembershipDto>> ForAppAsync(IReadOnlyList<MembershipDto> members, Guid appId, ISubjectIds subjects, CancellationToken ct)
    {
        IReadOnlyDictionary<Guid, Guid> ids = await subjects.SubjectsAsync(appId, [.. members.Select(m => m.UserId)], ct).ConfigureAwait(false);
        return [.. members.Select(m => m with { UserId = ids[m.UserId] })];
    }

    private static IResult NotAnAdminThere() => Results.NotFound(new { error = "The organisation does not exist, or that person is not an admin there." });

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
