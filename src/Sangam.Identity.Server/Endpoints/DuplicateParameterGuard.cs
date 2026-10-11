using System.Text.Json;
using Microsoft.Extensions.Primitives;

namespace Sangam.Identity.Server.Endpoints;

/// <summary>
/// rc.8 (ASVS V5.1.1, HTTP parameter pollution): OAuth 2.0 says a request parameter must not appear more than once
/// (RFC 6749 section 3.1). OpenIddict accepts some repeats and reads the first, so a request to <c>/connect/*</c> that names
/// any parameter twice within the query, or twice within the form, is refused here with <c>invalid_request</c> before
/// OpenIddict sees it. The parameters the protocol does allow more than once (<c>resource</c>, <c>audience</c>) are left
/// alone. A name in both the query and the form is allowed: OpenIddict reads only the form of a POST, and Sangam's own
/// sign-out confirmation posts its fields back to the address that carried them.
/// </summary>
public static partial class DuplicateParameterGuard
{
    // RFC 8707 resource indicators and OpenIddict's audience may legitimately repeat.
    private static readonly HashSet<string> Repeatable = new(StringComparer.Ordinal) { "resource", "audience" };

    /// <summary>Adds the guard to the pipeline, in front of OpenIddict.</summary>
    /// <param name="app">Application builder.</param>
    /// <returns>The same builder.</returns>
    public static IApplicationBuilder UseSangamDuplicateParameterGuard(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/connect", StringComparison.OrdinalIgnoreCase))
            {
                string? repeated = Repeated(context.Request.Query);
                if (repeated is null && context.Request.HasFormContentType)
                {
                    IFormCollection form = await context.Request.ReadFormAsync(context.RequestAborted).ConfigureAwait(false);
                    repeated = Repeated(form);
                }

                if (repeated is not null)
                {
                    ILogger logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(DuplicateParameterGuard).FullName!);
                    Log.Refused(logger, context.Request.Path.Value, repeated);
                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                    context.Response.ContentType = "application/json";
                    context.Response.Headers.CacheControl = "no-store";
                    await context.Response.WriteAsync(
                        JsonSerializer.Serialize(new Dictionary<string, string>
                        {
                            ["error"] = "invalid_request",
                            ["error_description"] = $"The '{repeated}' parameter must not be sent more than once.",
                        }),
                        context.RequestAborted).ConfigureAwait(false);
                    return;
                }
            }

            await next(context).ConfigureAwait(false);
        });
    }

    private static string? Repeated(IEnumerable<KeyValuePair<string, StringValues>> parameters)
        => parameters.Where(p => p.Value.Count > 1 && !Repeatable.Contains(p.Key)).Select(p => p.Key).FirstOrDefault();

    private static partial class Log
    {
        [LoggerMessage(EventId = 5101, Level = LogLevel.Warning, Message = "Refused a request to {Path}: the '{Parameter}' parameter was sent more than once.")]
        public static partial void Refused(ILogger logger, string? path, string parameter);
    }
}
