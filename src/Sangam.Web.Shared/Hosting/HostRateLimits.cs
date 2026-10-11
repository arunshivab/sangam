using System.Globalization;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Sangam.Web.Shared.Localization;

namespace Sangam.Web.Shared.Hosting;

/// <summary>
/// rc.9 (ASVS V11.1.4): limits on the portal and the two consoles, as the identity server already has. Each signed-in person
/// (or, before sign-in, each address) may make <c>Sangam:RateLimit:HostRequestsPerMinute</c> requests a minute (default
/// 600), of which <c>Sangam:RateLimit:HostPostsPerMinute</c> may change something (default 60). Static files, the Blazor circuit (its own limits apply) and health
/// checks are not counted. Over the limit the answer is 429 with <c>Retry-After</c>. In memory and per host.
/// </summary>
public static class HostRateLimits
{
    private const string Message = "Too many requests. Please wait a minute and try again."; // i18n-key

    private static readonly string[] Uncounted = ["/_blazor", "/_framework", "/_content", "/css", "/js", "/img", "/fonts", "/favicon", "/health"];

    /// <summary>Registers the limits; <c>Sangam:RateLimit:Enabled</c> (default true) turns them off.</summary>
    /// <param name="services">Service collection.</param>
    /// <param name="configuration">Configuration.</param>
    /// <returns>The same collection.</returns>
    public static IServiceCollection AddSangamHostRateLimits(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        bool enabled = configuration.GetValue("Sangam:RateLimit:Enabled", true);
        int requests = configuration.GetValue("Sangam:RateLimit:HostRequestsPerMinute", 600);
        int posts = configuration.GetValue("Sangam:RateLimit:HostPostsPerMinute", 60);

        services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.OnRejected = async (context, cancellationToken) =>
            {
                context.HttpContext.Response.Headers.RetryAfter = "60";
                context.HttpContext.Response.ContentType = "text/plain; charset=utf-8";
                string text = TextCatalogue.Translate(CultureInfo.CurrentUICulture.Name, Message) ?? Message;
                await context.HttpContext.Response.WriteAsync(text, cancellationToken).ConfigureAwait(false);
            };
            o.GlobalLimiter = PartitionedRateLimiter.CreateChained(
                PartitionedRateLimiter.Create<HttpContext, string>(context => Partition(context, enabled, requests, postsOnly: false)),
                PartitionedRateLimiter.Create<HttpContext, string>(context => Partition(context, enabled, posts, postsOnly: true)));
        });
        return services;
    }

    /// <summary>The key a request is counted under: the signed-in person, else the address.</summary>
    /// <param name="context">The request.</param>
    /// <returns>The key.</returns>
    public static string Key(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        string? subject = context.User.FindFirstValue("sub") ?? context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        return subject is not null ? "user:" + subject : "ip:" + (context.Connection.RemoteIpAddress?.ToString() ?? "unknown");
    }

    private static RateLimitPartition<string> Partition(HttpContext context, bool enabled, int perMinute, bool postsOnly)
    {
        string path = context.Request.Path.Value ?? string.Empty;
        if (!enabled
            || (postsOnly && !HttpMethods.IsPost(context.Request.Method) && !HttpMethods.IsPut(context.Request.Method) && !HttpMethods.IsPatch(context.Request.Method) && !HttpMethods.IsDelete(context.Request.Method))
            || Uncounted.Any(p => path.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
        {
            return RateLimitPartition.GetNoLimiter("none");
        }

        return RateLimitPartition.GetFixedWindowLimiter(Key(context), _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = perMinute,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
        });
    }
}
