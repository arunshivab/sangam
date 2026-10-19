using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using OpenIddict.Abstractions;
using OpenIddict.Validation.AspNetCore;
using Sangam.Identity.Application.Apps;
using Sangam.Identity.Application.Devices;
using Sangam.Identity.Application.Tenancy;
using Sangam.Identity.Server.Authentication;
using Sangam.Shared.Constants;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Sangam.Identity.Server.Api;

/// <summary>rc.12 (M12): why the application ends emergency access early.</summary>
/// <param name="Reason">The reason.</param>
public sealed record EmergencyEndRequest(string? Reason);

/// <summary>
/// rc.12: calls an application makes with the person's own access token (not its client credentials), so it can act only
/// on the sign-in it holds: lock the person's session (T1), end emergency access early (M12).
/// </summary>
public static class SessionEndpoints
{
    /// <summary>Maps <c>/api/v1/sessions/...</c>.</summary>
    /// <param name="endpoints">Endpoint route builder.</param>
    /// <returns>The same builder.</returns>
    public static IEndpointRouteBuilder MapSessionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        AuthorizationPolicy personToken = new AuthorizationPolicyBuilder(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme).RequireAuthenticatedUser().Build();
        RouteGroupBuilder api = endpoints.MapGroup("/api/v1/sessions").RequireAuthorization(personToken).DisableAntiforgery().RequireRateLimiting(AuthRateLimiting.ApiPolicy);

        // T1: the application's idle timer ran out. The session is locked on every application; on an enrolled shared device
        // the person comes back with their PIN, anywhere else with a full sign-in.
        api.MapPost("/lock", async (ClaimsPrincipal caller, IAppDirectory apps, ISubjectIds subjects, ISharedDeviceService devices, CancellationToken ct) =>
        {
            (AppSummary? app, Guid? userId, Guid? sessionId) = await PersonAsync(caller, apps, subjects, ct).ConfigureAwait(false);
            if (app is null || userId is null || sessionId is null)
            {
                return Results.Json(new { error = "Send the person's own Sangam access token, issued to your application, from a sign-in (it carries sid)." }, statusCode: StatusCodes.Status401Unauthorized);
            }

            return await devices.LockAsync(sessionId.Value, userId.Value, app.Id, ct).ConfigureAwait(false)
                ? Results.NoContent()
                : Results.NotFound(new { error = "That sign-in has already ended." });
        }).WithName("LockSession");

        // M12: the application ends emergency access early, with a reason (the patient left the ward, care handed over, the
        // emergency is over). Tokens refreshed after this no longer carry it.
        api.MapPost("/emergency/end", async (EmergencyEndRequest input, ClaimsPrincipal caller, IAppDirectory apps, ISubjectIds subjects, IEmergencyAccessService emergencies, CancellationToken ct) =>
        {
            (AppSummary? app, Guid? userId, Guid? sessionId) = await PersonAsync(caller, apps, subjects, ct).ConfigureAwait(false);
            if (app is null || userId is null || sessionId is null)
            {
                return Results.Json(new { error = "Send the person's own Sangam access token, issued to your application, from a sign-in (it carries sid)." }, statusCode: StatusCodes.Status401Unauthorized);
            }

            if (string.IsNullOrWhiteSpace(input?.Reason))
            {
                return Results.BadRequest(new { error = "Say why emergency access ends (reason)." });
            }

            return await emergencies.EndAsync(userId.Value, app.Id, sessionId.Value, input.Reason, ct).ConfigureAwait(false)
                ? Results.NoContent()
                : Results.NotFound(new { error = "No emergency access is in progress for this sign-in." });
        }).WithName("EndEmergencyAccess");

        return endpoints;
    }

    /// <summary>The application, person and session behind a person's access token.</summary>
    /// <param name="caller">The validated token.</param>
    /// <param name="apps">App directory.</param>
    /// <param name="subjects">rc.14: the token names the person by its application's own id.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The parts, or nulls.</returns>
    internal static async Task<(AppSummary? App, Guid? UserId, Guid? SessionId)> PersonAsync(ClaimsPrincipal caller, IAppDirectory apps, ISubjectIds subjects, CancellationToken ct)
    {
        AppSummary? app = await Endpoints.ConnectEndpoints.SubjectAppAsync(caller, apps, ct).ConfigureAwait(false);
        Guid? userId = app is not null && Guid.TryParse(caller.GetClaim(Claims.Subject), out Guid u) ? await subjects.UserAsync(app.Id, u, ct).ConfigureAwait(false) : null;
        // rc.14: the token's sid is the application's own for the session.
        Guid? sessionId = app is not null && userId is Guid person && Guid.TryParse(caller.GetClaim(SangamClaims.SessionId), out Guid s) ? await subjects.SessionAsync(app.Id, person, s, ct).ConfigureAwait(false) : null;
        return (app, userId, sessionId);
    }
}
