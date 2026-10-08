using System.Text.Json;
using Microsoft.AspNetCore;
using OpenIddict.Server;
using Sangam.Identity.Application.Abstractions;
using Sangam.Identity.Domain;
using Sangam.Identity.Domain.Enums;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Sangam.Identity.Server.Endpoints;

/// <summary>
/// R7 (ASVS V7.2.1, V11.1.7): the token, introspection and revocation endpoints are handled inside OpenIddict, so a
/// refused client secret or a spent, stolen or replayed code or refresh token left no trace in the audit log. This
/// records every such refusal — never the secret or the token itself — as <see cref="AuditActions.TokenRefused"/>.
/// </summary>
public static class TokenRefusalAudit
{
    /// <summary>The OAuth errors recorded: a client that failed to authenticate, or a grant that was not accepted.</summary>
    private static readonly HashSet<string> Recorded = new(StringComparer.Ordinal) { Errors.InvalidClient, Errors.InvalidGrant, Errors.UnauthorizedClient };

    /// <summary>Registers the handlers on the three endpoints.</summary>
    /// <param name="builder">OpenIddict's server builder.</param>
    public static OpenIddictServerBuilder AddTokenRefusalAudit(this OpenIddictServerBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.AddEventHandler<OpenIddictServerEvents.ApplyTokenResponseContext>(h => h.UseInlineHandler(c => WriteAsync(c.Transaction, "token", c.Response?.Error)));
        builder.AddEventHandler<OpenIddictServerEvents.ApplyIntrospectionResponseContext>(h => h.UseInlineHandler(c => WriteAsync(c.Transaction, "introspection", c.Response?.Error)));
        builder.AddEventHandler<OpenIddictServerEvents.ApplyRevocationResponseContext>(h => h.UseInlineHandler(c => WriteAsync(c.Transaction, "revocation", c.Response?.Error)));
        return builder;
    }

    private static async ValueTask WriteAsync(OpenIddictServerTransaction transaction, string endpoint, string? error)
    {
        if (error is null || !Recorded.Contains(error) || transaction.GetHttpRequest()?.HttpContext is not { } http)
        {
            return;
        }

        IAuditWriter audit = http.RequestServices.GetRequiredService<IAuditWriter>();
        await audit.WriteAsync(
            new AuditEntry(
                AuditActions.TokenRefused,
                AuditActorType.Api,
                TargetType: "client",
                Metadata: JsonSerializer.Serialize(new Dictionary<string, string?>
                {
                    ["endpoint"] = endpoint,
                    ["error"] = error,
                    ["client_id"] = transaction.Request?.ClientId,
                    ["grant_type"] = transaction.Request?.GrantType,
                }),
                IpAddress: http.Connection.RemoteIpAddress?.ToString(),
                UserAgent: http.Request.Headers.UserAgent.ToString()),
            http.RequestAborted).ConfigureAwait(false);
    }
}
