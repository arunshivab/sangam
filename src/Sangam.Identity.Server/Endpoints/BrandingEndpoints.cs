using Sangam.Identity.Application.Customisation;

namespace Sangam.Identity.Server.Endpoints;

/// <summary>Serves uploaded logos (PR-19).</summary>
public static class BrandingEndpoints
{
    /// <summary>
    /// Maps <c>/branding/logo/{id}</c>. A logo is served as a picture only: <c>nosniff</c>, and a
    /// Content-Security-Policy that sandboxes it and forbids every script and outside load. rc.16 (ASVS V1.12.2): every
    /// logo is a PNG (SVGs are no longer accepted or served), so nothing scriptable comes from Sangam's origin. The
    /// address carries the image's hash, so it is cached for a year.
    /// </summary>
    /// <param name="endpoints">Endpoints.</param>
    public static IEndpointRouteBuilder MapBrandingEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        endpoints.MapGet("/branding/logo/{id:guid}", async (Guid id, HttpContext context, ICustomisationService customisation, CancellationToken cancellationToken) =>
        {
            BrandingLogo? logo = await customisation.GetLogoAsync(id, cancellationToken).ConfigureAwait(false);
            if (logo is null)
            {
                return Results.NotFound();
            }

            context.Response.Headers.XContentTypeOptions = "nosniff";
            context.Response.Headers.ContentSecurityPolicy = "default-src 'none'; style-src 'unsafe-inline'; sandbox";
            context.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
            context.Response.Headers["Cross-Origin-Resource-Policy"] = "cross-origin";
            return Results.Bytes(logo.Content, logo.ContentType);
        }).AllowAnonymous();
        return endpoints;
    }
}
