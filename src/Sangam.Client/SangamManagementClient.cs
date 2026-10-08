using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Sangam.Client;

/// <summary>
/// Sangam's management API for your application's own back end (roles, organisations, memberships, attribute values),
/// with client-credentials tokens fetched and cached for you. Needs a confidential client allowed <c>sangam.manage</c>.
/// </summary>
public sealed class SangamManagementClient : IDisposable
{
    /// <summary>The name of the HTTP client it uses.</summary>
    public const string HttpClientName = "sangam.management";

    private readonly IHttpClientFactory _http;
    private readonly SangamOptions _options;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly Dictionary<string, (string Token, DateTimeOffset Expires)> _cache = new(StringComparer.Ordinal);

    /// <summary>Initialises the client.</summary>
    /// <param name="http">HTTP clients.</param>
    /// <param name="options">Sangam settings (authority, client id and secret).</param>
    public SangamManagementClient(IHttpClientFactory http, SangamOptions options)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <inheritdoc />
    public void Dispose() => _lock.Dispose();

    /// <summary>A client-credentials access token for the scope, reused until a minute before it expires.</summary>
    /// <param name="scope">The scope, for example <c>sangam.manage</c>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<string> GetTokenAsync(string scope = "sangam.manage", CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cache.TryGetValue(scope, out (string Token, DateTimeOffset Expires) cached) && cached.Expires > DateTimeOffset.UtcNow.AddMinutes(1))
            {
                return cached.Token;
            }

            SangamOptions o = _options;
            using FormUrlEncodedContent form = new(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = o.ClientId,
                ["client_secret"] = o.ClientSecret ?? string.Empty,
                ["scope"] = scope,
            });
            using HttpResponseMessage response = await _http.CreateClient(HttpClientName).PostAsync(new Uri(o.Authority.TrimEnd('/') + "/connect/token"), form, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            using JsonDocument body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), cancellationToken: cancellationToken).ConfigureAwait(false);
            string token = body.RootElement.GetProperty("access_token").GetString()!;
            int lifetime = body.RootElement.TryGetProperty("expires_in", out JsonElement e) ? e.GetInt32() : 300;
            _cache[scope] = (token, DateTimeOffset.UtcNow.AddSeconds(lifetime));
            return token;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Creates or updates a role.</summary>
    /// <param name="code">The role code.</param>
    /// <param name="displayName">Its name.</param>
    /// <param name="permissions">Its permissions.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<JsonNode?> UpsertRoleAsync(string code, string displayName, IReadOnlyList<string> permissions, CancellationToken cancellationToken = default)
        => SendAsync(HttpMethod.Put, "roles/" + Uri.EscapeDataString(code), new { displayName, description = (string?)null, permissions, orgId = (Guid?)null }, cancellationToken);

    /// <summary>Creates or updates an organisation.</summary>
    /// <param name="id">Its id (you choose it).</param>
    /// <param name="name">Its name.</param>
    /// <param name="type">Its type, for example <c>hospital</c>.</param>
    /// <param name="parentId">Its parent, or none for a root.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<JsonNode?> UpsertOrganisationAsync(Guid id, string name, string type, Guid? parentId = null, CancellationToken cancellationToken = default)
        => SendAsync(HttpMethod.Put, $"orgs/{id:D}", new { name, type, parentId, metadata = (string?)null }, cancellationToken);

    /// <summary>Gives a person a role at an organisation.</summary>
    /// <param name="orgId">The organisation.</param>
    /// <param name="userId">The person (their <c>sub</c>).</param>
    /// <param name="role">The role code.</param>
    /// <param name="appliesToDescendants">Whether it reaches the organisation's children.</param>
    /// <param name="expiresAt">When it ends, for a time-limited role.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<JsonNode?> UpsertMembershipAsync(Guid orgId, Guid userId, string role, bool appliesToDescendants = false, DateTimeOffset? expiresAt = null, CancellationToken cancellationToken = default)
        => SendAsync(HttpMethod.Put, $"orgs/{orgId:D}/members/{userId:D}", new { role, appliesToDescendants, expiresAt }, cancellationToken);

    /// <summary>The values your application keeps about a person.</summary>
    /// <param name="userId">The person.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<JsonNode?> GetAttributesAsync(Guid userId, CancellationToken cancellationToken = default)
        => SendAsync(HttpMethod.Get, $"users/{userId:D}/attributes", null, cancellationToken);

    /// <summary>Sets values your application keeps about a person; an empty value clears one.</summary>
    /// <param name="userId">The person.</param>
    /// <param name="values">Keys and values.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<JsonNode?> SetAttributesAsync(Guid userId, IReadOnlyDictionary<string, string?> values, CancellationToken cancellationToken = default)
        => SendAsync(HttpMethod.Put, $"users/{userId:D}/attributes", values, cancellationToken);

    /// <summary>Any management API call: <paramref name="path"/> is relative to <c>/api/v1/</c>.</summary>
    /// <param name="method">The method.</param>
    /// <param name="path">For example <c>roles/nurse</c>.</param>
    /// <param name="body">A JSON body, or none.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<JsonNode?> SendAsync(HttpMethod method, string path, object? body, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(path);
        string token = await GetTokenAsync("sangam.manage", cancellationToken).ConfigureAwait(false);
        using HttpRequestMessage request = new(method, _options.Authority.TrimEnd('/') + "/api/v1/" + path.TrimStart('/'));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        using HttpResponseMessage response = await _http.CreateClient(HttpClientName).SendAsync(request, cancellationToken).ConfigureAwait(false);
        string text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Sangam's management API answered {(int)response.StatusCode} to {method} {path}: {text}", null, response.StatusCode);
        }

        return text.Length == 0 ? null : JsonNode.Parse(text);
    }
}
