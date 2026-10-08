using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Sangam.Identity.Application.Abstractions;

namespace Sangam.Identity.Infrastructure;

/// <summary>Records each request’s client in the scoped <see cref="ClientContext"/> (OI-039).</summary>
public static class ClientContextMiddleware
{
    /// <summary>Adds the middleware. Place it after forwarded-headers handling.</summary>
    /// <param name="app">The application.</param>
    public static IApplicationBuilder UseSangamClientContext(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.Use(async (context, next) =>
        {
            context.RequestServices.GetRequiredService<ClientContext>().Set(context.Connection.RemoteIpAddress?.ToString(), context.Request.Headers.UserAgent.ToString());
            await next(context).ConfigureAwait(false);
        });
    }
}
