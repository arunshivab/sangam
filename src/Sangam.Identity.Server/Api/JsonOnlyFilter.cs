using Microsoft.Net.Http.Headers;

namespace Sangam.Identity.Server.Api;

/// <summary>
/// rc.8 (ASVS V13.1.5, V13.2.5): the management API speaks JSON only. A request carrying a body of any other type is refused
/// with 415, and a request whose <c>Accept</c> header rules JSON out is refused with 406, before the endpoint runs.
/// </summary>
public static class JsonOnlyFilter
{
    /// <summary>The endpoint filter.</summary>
    /// <param name="context">Invocation context.</param>
    /// <param name="next">The rest of the pipeline.</param>
    /// <returns>The endpoint's result, or the refusal.</returns>
    public static async ValueTask<object?> FilterAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);
        HttpRequest request = context.HttpContext.Request;

        bool hasBody = request.ContentLength > 0 || request.Headers.TransferEncoding.Count > 0;
        if (hasBody && !IsJson(request.ContentType))
        {
            return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);
        }

        if (!AcceptsJson(request.Headers.Accept))
        {
            return Results.StatusCode(StatusCodes.Status406NotAcceptable);
        }

        return await next(context).ConfigureAwait(false);
    }

    /// <summary>Whether a content type is JSON (<c>application/json</c> or a <c>+json</c> type).</summary>
    /// <param name="contentType">The Content-Type header.</param>
    /// <returns><see langword="true"/> for JSON.</returns>
    public static bool IsJson(string? contentType)
        => MediaTypeHeaderValue.TryParse(contentType, out MediaTypeHeaderValue? type)
            && type.MediaType.HasValue
            && (type.MediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase)
                || (type.MediaType.StartsWith("application/", StringComparison.OrdinalIgnoreCase) && type.MediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase)));

    /// <summary>Whether an Accept header lets the API answer with JSON. No header, or one that cannot be read, counts as yes.</summary>
    /// <param name="accept">The Accept header values.</param>
    /// <returns><see langword="true"/> when JSON is acceptable.</returns>
    public static bool AcceptsJson(Microsoft.Extensions.Primitives.StringValues accept)
    {
        if (accept.Count == 0 || !MediaTypeHeaderValue.TryParseList(accept, out IList<MediaTypeHeaderValue>? types) || types.Count == 0)
        {
            return true;
        }

        return types.Any(t => (t.Quality ?? 1) > 0
            && (t.MediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase)
                || t.MediaType.Equals("application/*", StringComparison.OrdinalIgnoreCase)
                || t.MediaType.Equals("*/*", StringComparison.OrdinalIgnoreCase)));
    }
}
