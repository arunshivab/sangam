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

        services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.OnRejected = async (ctx, ct) =>
            {
                ctx.HttpContext.Response.Headers.RetryAfter = "60";
                ctx.HttpContext.Response.ContentType = "text/plain; charset=utf-8";
                await ctx.HttpContext.Response.WriteAsync("Too many attempts. Please wait a minute and try again.", ct).ConfigureAwait(false);
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
        });

        return services;
    }
}
