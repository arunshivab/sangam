using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Http.HttpResults;
using OpenIddict.Abstractions;
using Sangam.Identity.Application.Abstractions;
using Sangam.Identity.Domain;
using Sangam.Identity.Domain.Enums;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Sangam.Identity.Server.Api;

/// <summary>
/// R7 (ASVS V7.2.2): every refusal of an authenticated caller at the management API is recorded in the audit log as
/// <c>access.denied</c> — a token without the <c>sangam.manage</c> scope (refused by the authorization policy before
/// the endpoint runs), and an application that is unknown or disabled (refused by the endpoint). Unauthenticated
/// requests are not recorded, so a flood of anonymous calls cannot fill the log; the rate limiter meets those.
/// </summary>
public sealed class AccessDeniedAudit : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler _default = new();

    /// <inheritdoc />
    public async Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult authorizeResult)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(authorizeResult);
        if (authorizeResult.Forbidden && context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase))
        {
            await WriteAsync(context, "The token does not carry the scope this API needs.").ConfigureAwait(false);
        }

        await _default.HandleAsync(next, context, policy, authorizeResult).ConfigureAwait(false);
    }

    /// <summary>An endpoint filter that records the endpoint's own refusals (the calling application is unknown or disabled).</summary>
    /// <param name="context">The invocation.</param>
    /// <param name="next">The endpoint.</param>
    public static async ValueTask<object?> FilterAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);
        object? result = await next(context).ConfigureAwait(false);
        if (result is ForbidHttpResult)
        {
            await WriteAsync(context.HttpContext, "The calling application is not registered with Sangam or is disabled.").ConfigureAwait(false);
        }

        return result;
    }

    private static async Task WriteAsync(HttpContext context, string reason)
    {
        ClaimsPrincipal caller = context.User;
        IAuditWriter audit = context.RequestServices.GetRequiredService<IAuditWriter>();
        await audit.WriteAsync(
            new AuditEntry(
                AuditActions.AccessDenied,
                AuditActorType.Api,
                TargetType: "api",
                Metadata: JsonSerializer.Serialize(new Dictionary<string, string?>
                {
                    ["method"] = context.Request.Method,
                    ["path"] = context.Request.Path.Value,
                    ["client_id"] = caller.GetClaim(Claims.Subject) ?? caller.GetClaim(Claims.ClientId),
                    ["reason"] = reason,
                }),
                IpAddress: context.Connection.RemoteIpAddress?.ToString(),
                UserAgent: context.Request.Headers.UserAgent.ToString()),
            context.RequestAborted).ConfigureAwait(false);
    }
}
