using System.Security.Claims;
using System.Text.Json;
using OpenIddict.Abstractions;
using Sangam.Identity.Application.Abstractions;
using Sangam.Identity.Domain;
using Sangam.Identity.Domain.Enums;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Sangam.Identity.Server.Api;

/// <summary>
/// rc.9 (ASVS V7.1.3, V13.2.2): a request whose body or parameters cannot be read — malformed JSON, a field the API does not
/// know, a missing required value — is refused with 400 as before, and is now also a security event. On the management API
/// it is written to the audit log as <c>api.request.invalid</c> (counted by the never-normal alerts, V11.1.7); on any other
/// endpoint it is logged. The body is never recorded.
/// </summary>
public static partial class BadRequestAudit
{
    /// <summary>The reason recorded: the framework says only that binding failed, and the body is never kept.</summary>
    public const string Reason = "The request body or parameters could not be read (malformed JSON, a field the API does not know, or a missing required value).";

    /// <summary>
    /// Records the binding failures. When the framework cannot bind a request it answers 400 with no body before the endpoint
    /// runs; that answer (400, no content type) is recognised here, given a short problem body, logged, and — on the
    /// management API — audited. The endpoints' own 400s carry a JSON body and are left alone.
    /// </summary>
    /// <param name="app">Application builder.</param>
    /// <returns>The same builder.</returns>
    public static IApplicationBuilder UseSangamBadRequestAudit(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.Use(async (context, next) =>
        {
            await next(context).ConfigureAwait(false);
            if (context.Response.StatusCode != StatusCodes.Status400BadRequest || !string.IsNullOrEmpty(context.Response.ContentType))
            {
                return;
            }

            ILogger logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(BadRequestAudit).FullName!);
            Log.Refused(logger, context.Request.Method, context.Request.Path.Value);
            if (context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase))
            {
                await AuditAsync(context, Reason).ConfigureAwait(false);
            }

            if (!context.Response.HasStarted)
            {
                context.Response.ContentType = "application/problem+json";
                await context.Response.WriteAsync(
                    JsonSerializer.Serialize(new Dictionary<string, object> { ["status"] = 400, ["title"] = "The request could not be read.", ["detail"] = Reason }),
                    context.RequestAborted).ConfigureAwait(false);
            }
        });
    }

    private static async Task AuditAsync(HttpContext context, string reason)
    {
        ClaimsPrincipal caller = context.User;
        IAuditWriter audit = context.RequestServices.GetRequiredService<IAuditWriter>();
        await audit.WriteAsync(
            new AuditEntry(
                AuditActions.ApiRequestInvalid,
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

    private static partial class Log
    {
        [LoggerMessage(EventId = 5103, Level = LogLevel.Warning, Message = "Refused {Method} {Path}: the request body or parameters could not be read")]
        public static partial void Refused(ILogger logger, string method, string? path);
    }
}
