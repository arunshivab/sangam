using System.Threading.RateLimiting;

namespace Sangam.Identity.Server.Authentication;

/// <summary>
/// Per-IP limits on the POSTs that can be abused (sign-in, registration, code checks, reset).
/// GETs are never limited. In-memory and per node — good for one VM; a shared store comes with
/// multi-node deployment.
/// </summary>
public static class AuthRateLimiting
{
    /// <summary>Policy name applied to the Razor Pages.</summary>
    public const string PolicyName = "auth";

    /// <summary>Configuration section.</summary>
    public const string SectionName = "Sangam:RateLimit";

    /// <summary>Policy for <c>/connect/token</c>: guessing client secrets or codes (OI-035).</summary>
    public const string TokenPolicy = "token";

    /// <summary>Policy for <c>/connect/userinfo</c> (OI-035).</summary>
    public const string UserInfoPolicy = "userinfo";

    /// <summary>The protocol endpoints OpenIddict answers before routing (PR-20, PR-21).</summary>
    private static readonly HashSet<string> ProtocolPaths = new(StringComparer.OrdinalIgnoreCase) { "/connect/device", "/connect/par", "/connect/introspect", "/connect/revoke" };

    /// <summary>Policy for the management API, <c>/api/v1</c> (OI-035).</summary>
    public const string ApiPolicy = "api";

    /// <summary>Registers the limiter. <c>Sangam:RateLimit:PostsPerMinute</c> (default 20) and <c>Sangam:RateLimit:Enabled</c> (default true).</summary>
    /// <param name="services">Service collection.</param>
    /// <param name="configuration">Configuration.</param>
    /// <returns>The same collection.</returns>
    public static IServiceCollection AddAuthRateLimiting(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        bool enabled = configuration.GetValue("Sangam:RateLimit:Enabled", true);
        int postsPerMinute = configuration.GetValue("Sangam:RateLimit:PostsPerMinute", 20);
        int tokenPerMinute = configuration.GetValue("Sangam:RateLimit:TokenPerMinute", 60);
        int userInfoPerMinute = configuration.GetValue("Sangam:RateLimit:UserInfoPerMinute", 120);
        int apiPerMinute = configuration.GetValue("Sangam:RateLimit:ApiPerMinute", 300);

        services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.OnRejected = async (ctx, ct) =>
            {
                ctx.HttpContext.Response.Headers.RetryAfter = "60";
                ctx.HttpContext.Response.ContentType = "text/plain; charset=utf-8";
                string message = Pages.PageText.For(ctx.HttpContext)["Too many attempts. Please wait a minute and try again."];
                await ctx.HttpContext.Response.WriteAsync(message, ct).ConfigureAwait(false);
            };
            o.AddPolicy(PolicyName, ctx =>
            {
                if (!enabled || !HttpMethods.IsPost(ctx.Request.Method))
                {
                    return RateLimitPartition.GetNoLimiter("none");
                }

                string key = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = postsPerMinute,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                });
            });
            o.AddPolicy(TokenPolicy, ctx => PerIp(ctx, enabled, tokenPerMinute));
            o.AddPolicy(UserInfoPolicy, ctx => PerIp(ctx, enabled, userInfoPerMinute));
            o.AddPolicy(ApiPolicy, ctx => PerIp(ctx, enabled, apiPerMinute));

            // OpenIddict answers these itself, before routing, so no endpoint policy reaches them: device codes,
            // pushed requests, introspection and revocation are limited here, per address, like the token endpoint.
            o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx => ProtocolPaths.Contains(ctx.Request.Path.Value ?? string.Empty)
                ? PerIp(ctx, enabled, tokenPerMinute)
                : RateLimitPartition.GetNoLimiter("none"));
        });

        return services;
    }

    private static RateLimitPartition<string> PerIp(HttpContext ctx, bool enabled, int perMinute)
    {
        if (!enabled)
        {
            return RateLimitPartition.GetNoLimiter("none");
        }

        string key = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = perMinute,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
        });
    }
}
