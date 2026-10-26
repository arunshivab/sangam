using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Sangam.Web.Shared.Hosting;

/// <summary>
/// The answer every host gives (rc.16: the identity server too) when a request fails outside Development: a short page
/// written straight to the response, with no page behind the sign-in that could itself fail. The Blazor hosts used to
/// re-execute <c>/Error</c>, a page that did not exist and sat behind the sign-in; when Sangam could not be reached (the
/// usual cause), the error handler failed too and the visitor saw a bare 500.
/// <para>
/// rc.16 (ASVS V7.4.1): the page is <see cref="StatusPages"/>, the same one in look and language as the 404 page, and it
/// shows a reference to quote, the request's id. ASP.NET Core logs the failure with the same id as <c>RequestId</c> (the
/// hosts log JSON with scopes), so support can find the error from the reference alone.
/// </para>
/// </summary>
public static class ErrorPage
{
    /// <summary>Uses the plain error answer.</summary>
    /// <param name="app">The application.</param>
    public static IApplicationBuilder UseSangamErrorPage(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseExceptionHandler(error => error.Run(context =>
            StatusPages.WriteAsync(context, StatusCodes.Status500InternalServerError, context.TraceIdentifier)));
    }
}
