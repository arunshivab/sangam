using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Sangam.Identity.Infrastructure.Provisioning;

/// <summary>Signs a short-lived token Sangam presents to an application (the SCIM <c>sangam</c> authentication mode).</summary>
public interface IServiceTokenIssuer
{
    /// <summary>A token for <paramref name="audience"/>, signed with Sangam's published token keys.</summary>
    /// <param name="audience">Who it is for (the SCIM base address).</param>
    /// <param name="lifetime">How long it lasts.</param>
    string Issue(string audience, TimeSpan lifetime);
}

/// <summary>One SCIM call's outcome.</summary>
/// <param name="Status">HTTP status, or 0 when the call never got an answer.</param>
/// <param name="Body">The answer, parsed (null when empty or not JSON).</param>
/// <param name="Line">A short line for the delivery log, for example <c>POST /Users 201</c>.</param>
/// <param name="Error">What went wrong, with the start of the answer, or null.</param>
/// <param name="LatencyMs">How long it took.</param>
public sealed record ScimCall(int Status, JsonNode? Body, string Line, string? Error, int LatencyMs)
{
    /// <summary>Whether the call succeeded (2xx).</summary>
    public bool Ok => Status is >= 200 and < 300;
}

/// <summary>
/// A SCIM 2.0 client for one application's server (RFC 7644): JSON in <c>application/scim+json</c>, the application's
/// bearer token or a Sangam-signed one, and every call reduced to a line for the delivery log.
/// </summary>
public sealed class ScimClient
{
    /// <summary>The SCIM media type.</summary>
    public const string MediaType = "application/scim+json";

    /// <summary>SCIM's core user schema.</summary>
    public const string UserSchema = "urn:ietf:params:scim:schemas:core:2.0:User";

    /// <summary>SCIM's enterprise user extension.</summary>
    public const string EnterpriseSchema = "urn:ietf:params:scim:schemas:extension:enterprise:2.0:User";

    /// <summary>SCIM's group schema.</summary>
    public const string GroupSchema = "urn:ietf:params:scim:schemas:core:2.0:Group";

    /// <summary>SCIM's PATCH message schema.</summary>
    public const string PatchSchema = "urn:ietf:params:scim:api:messages:2.0:PatchOp";

    private readonly HttpClient _http;
    private readonly string _base;
    private readonly Func<string> _authorization;

    /// <summary>Initialises the client.</summary>
    /// <param name="http">An HTTP client made by <see cref="OutboundHttp"/>.</param>
    /// <param name="baseUrl">The SCIM base address.</param>
    /// <param name="bearer">Gives the bearer token for each call.</param>
    public ScimClient(HttpClient http, string baseUrl, Func<string> bearer)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _base = baseUrl.TrimEnd('/');
        _authorization = bearer ?? throw new ArgumentNullException(nameof(bearer));
    }

    /// <summary>GET.</summary>
    /// <param name="path">Path below the base, for example <c>/Users?filter=…</c>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<ScimCall> GetAsync(string path, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(path);
        return SendAsync(HttpMethod.Get, path, null, cancellationToken);
    }

    /// <summary>POST a resource.</summary>
    /// <param name="path">Path below the base.</param>
    /// <param name="body">The resource.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<ScimCall> PostAsync(string path, JsonObject body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(body);
        return SendAsync(HttpMethod.Post, path, body, cancellationToken);
    }

    /// <summary>PATCH with SCIM operations.</summary>
    /// <param name="path">Path below the base.</param>
    /// <param name="operations">The operations.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<ScimCall> PatchAsync(string path, JsonArray operations, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(operations);
        return SendAsync(HttpMethod.Patch, path, new JsonObject { ["schemas"] = new JsonArray(PatchSchema), ["Operations"] = operations }, cancellationToken);
    }

    /// <summary>DELETE.</summary>
    /// <param name="path">Path below the base.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<ScimCall> DeleteAsync(string path, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(path);
        return SendAsync(HttpMethod.Delete, path, null, cancellationToken);
    }

    /// <summary>A filter value quoted as SCIM requires.</summary>
    /// <param name="value">The value.</param>
    public static string Quote(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }

    private async Task<ScimCall> SendAsync(HttpMethod method, string path, JsonObject? body, CancellationToken cancellationToken)
    {
        string shown = path.Split('?', 2)[0];
        Stopwatch watch = Stopwatch.StartNew();
        try
        {
            using HttpRequestMessage request = new(method, new Uri(_base + path, UriKind.Absolute));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _authorization());
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(MediaType));
            if (body is not null)
            {
                request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, MediaType);
            }

            using HttpResponseMessage response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            string text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            int ms = (int)watch.ElapsedMilliseconds;
            int status = (int)response.StatusCode;
            JsonNode? parsed = null;
            if (text.Length > 0)
            {
                try
                {
                    parsed = JsonNode.Parse(text);
                }
                catch (JsonException)
                {
                    parsed = null;
                }
            }

            string line = $"{method.Method} {shown} {status.ToString(CultureInfo.InvariantCulture)}";
            string? error = response.IsSuccessStatusCode ? null : line + ": " + (text.Length > 300 ? text[..300] : text);
            return new ScimCall(status, parsed, line, error, ms);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or UriFormatException)
        {
            string line = $"{method.Method} {shown} failed";
            return new ScimCall(0, null, line, line + ": " + ex.Message, (int)watch.ElapsedMilliseconds);
        }
    }
}
