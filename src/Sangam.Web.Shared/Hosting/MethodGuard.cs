using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Sangam.Web.Shared.Hosting;

/// <summary>
/// rc.8 (ASVS V14.5.1): every Sangam host answers only the HTTP methods it uses. A request with any other method (TRACE,
/// CONNECT, a made-up verb) is refused with 405 before anything else runs, and every 405 — refused here or by routing,
/// for a known address called with the wrong method — is logged, so probing shows up in the logs.
/// </summary>
public static partial class MethodGuard
{
    private static readonly HashSet<string> Allowed = new(StringComparer.OrdinalIgnoreCase)
    {
        HttpMethods.Get,
        HttpMethods.Head,
        HttpMethods.Post,
        HttpMethods.Put,
        HttpMethods.Patch,
        HttpMethods.Delete,
        HttpMethods.Options,
    };

    /// <summary>Whether Sangam answers <paramref name="method"/> at all.</summary>
    /// <param name="method">The request method.</param>
    /// <returns><see langword="true"/> for the methods Sangam uses.</returns>
    public static bool IsAllowed(string method) => Allowed.Contains(method);

    /// <summary>Adds the guard to the pipeline; put it early, straight after the forwarded headers.</summary>
    /// <param name="app">Application builder.</param>
    /// <returns>The same builder.</returns>
    public static IApplicationBuilder UseSangamMethodGuard(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.Use(async (context, next) =>
        {
            if (!IsAllowed(context.Request.Method))
            {
                context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
                context.Response.Headers.Allow = string.Join(", ", Allowed);
                Write(context, refusedHere: true);
                return;
            }

            await next(context).ConfigureAwait(false);
            if (context.Response.StatusCode == StatusCodes.Status405MethodNotAllowed)
            {
                Write(context, refusedHere: false);
            }
        });
    }

    private static void Write(HttpContext context, bool refusedHere)
    {
        ILogger logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(MethodGuard).FullName!);

        // The method is clipped: it comes from the caller and could be anything.
        string method = context.Request.Method.Length > 16 ? context.Request.Method[..16] : context.Request.Method;
        Log.NotAllowed(logger, method, context.Request.Path.Value, context.Connection.RemoteIpAddress?.ToString(), refusedHere ? "not used by Sangam" : "not allowed at this address");
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 5100, Level = LogLevel.Warning, Message = "Refused {Method} {Path} from {RemoteIp}: method {Reason}.")]
        public static partial void NotAllowed(ILogger logger, string method, string? path, string? remoteIp, string reason);
    }
}
