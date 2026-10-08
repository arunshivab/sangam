using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using OpenIddict.Abstractions;
using OpenIddict.Validation.AspNetCore;
using Sangam.Identity.Application.Apps;
using Sangam.Identity.Application.Attributes;
using Sangam.Identity.Application.Signatures;
using Sangam.Identity.Application.Tenancy;
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

        RouteGroupBuilder api = endpoints.MapGroup("/api/v1").RequireAuthorization(PolicyName).DisableAntiforgery().RequireRateLimiting(AuthRateLimiting.ApiPolicy);

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

        api.MapGet("/orgs/{orgId:guid}/members", async (Guid orgId, ClaimsPrincipal caller, IAppDirectory apps, IManagementService mgmt, CancellationToken ct) =>
        {
            AppSummary? app = await CallerAppAsync(caller, apps, ct).ConfigureAwait(false);
            return app is null ? Results.Forbid() : Results.Ok(await mgmt.ListMembersAsync(app.Id, orgId, ct).ConfigureAwait(false));
        }).WithName("ListMembers");

        api.MapPut("/orgs/{orgId:guid}/members/{userId:guid}", async (Guid orgId, Guid userId, MembershipUpsert input, ClaimsPrincipal caller, IAppDirectory apps, IManagementService mgmt, CancellationToken ct) =>
        {
            AppSummary? app = await CallerAppAsync(caller, apps, ct).ConfigureAwait(false);
            return app is null ? Results.Forbid() : ToResult(await mgmt.UpsertMembershipAsync(app.Id, orgId, userId, input, ManagementActor.Api, ct).ConfigureAwait(false));
        }).WithName("UpsertMembership");

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
