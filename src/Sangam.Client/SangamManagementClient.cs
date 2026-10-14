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

    /// <summary>rc.12: the header that carries the acting admin's own access token on staff calls.</summary>
    public const string ActingAdminHeader = "Sangam-Acting-Admin";

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

    /// <summary>
    /// Gives a person a role at an organisation. rc.5: only someone who already uses your application (has signed in to
    /// it); anyone else answers 404 — add them as staff with <see cref="AddStaffAsync"/> instead. rc.12: a role holds only
    /// where it is given, so <paramref name="appliesToDescendants"/> must be <see langword="false"/>.
    /// </summary>
    /// <param name="orgId">The organisation.</param>
    /// <param name="userId">The person (their <c>sub</c>).</param>
    /// <param name="role">The role code.</param>
    /// <param name="appliesToDescendants">Must be <see langword="false"/> since rc.12.</param>
    /// <param name="expiresAt">When it ends, for a time-limited role.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<JsonNode?> UpsertMembershipAsync(Guid orgId, Guid userId, string role, bool appliesToDescendants = false, DateTimeOffset? expiresAt = null, CancellationToken cancellationToken = default)
        => SendAsync(HttpMethod.Put, $"orgs/{orgId:D}/members/{userId:D}", new { role, appliesToDescendants, expiresAt }, cancellationToken);

    /// <summary>
    /// rc.12: sets every role a person holds at an organisation (a doctor who is also the department's administrator, say);
    /// an empty list keeps them there as staff, for a customer whose roles your application keeps.
    /// </summary>
    /// <param name="orgId">The organisation.</param>
    /// <param name="userId">The person (their <c>sub</c>).</param>
    /// <param name="roles">Every role code they hold there.</param>
    /// <param name="expiresAt">When they end, if they do.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<JsonNode?> SetRolesAsync(Guid orgId, Guid userId, IReadOnlyList<string> roles, DateTimeOffset? expiresAt = null, CancellationToken cancellationToken = default)
        => SendAsync(HttpMethod.Put, $"orgs/{orgId:D}/members/{userId:D}", new { roles, expiresAt }, cancellationToken);

    /// <summary>
    /// rc.12: the check before adding a person — which details match an existing Sangam account and which differ, never
    /// what Sangam holds. Done for one of your admins: <paramref name="adminAccessToken"/> is the access token Sangam gave
    /// your application when that admin signed in. At most five checks per person per admin per day.
    /// </summary>
    /// <param name="orgId">The organisation.</param>
    /// <param name="adminAccessToken">The admin's own access token, issued to your application.</param>
    /// <param name="person">The details the admin entered.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<JsonNode?> CheckStaffAsync(Guid orgId, string adminAccessToken, SangamStaffPerson person, CancellationToken cancellationToken = default)
        => SendForAdminAsync(HttpMethod.Post, $"orgs/{orgId:D}/staff/check", person, adminAccessToken, cancellationToken);

    /// <summary>
    /// rc.12: adds one person to one organisation, for one of your admins (who must be verified with DigiLocker). Send their
    /// details (they get an activation code to hand over in person, returned once as <c>activationCode</c>), the join code
    /// they made in their Sangam portal, the <c>staffRef</c> an earlier call returned, or the <c>sub</c> of someone already
    /// in this customer. The answer's <c>status</c> is <c>added</c>, <c>waiting</c> or <c>needs_confirmation</c> (send the
    /// same call again with <c>confirmed</c> once the admin has checked).
    /// </summary>
    /// <param name="orgId">The organisation.</param>
    /// <param name="adminAccessToken">The admin's own access token, issued to your application.</param>
    /// <param name="addition">Who, and with which roles.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<JsonNode?> AddStaffAsync(Guid orgId, string adminAccessToken, SangamStaffAddition addition, CancellationToken cancellationToken = default)
        => SendForAdminAsync(HttpMethod.Post, $"orgs/{orgId:D}/staff", addition, adminAccessToken, cancellationToken);

    /// <summary>
    /// Where a person's account went if they merged it into another of theirs (rc.7): the surviving account's <c>sub</c>,
    /// or <see langword="null"/> when the id was not merged or your application never knew it. Use it on
    /// <c>account.merged</c>, or for an id that stopped signing in, to move what you keep to the surviving id.
    /// </summary>
    /// <param name="userId">The id (<c>sub</c>) your application holds.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<Guid?> GetMergedIntoAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        try
        {
            JsonNode? answer = await SendAsync(HttpMethod.Get, $"users/{userId:D}/merged", null, cancellationToken).ConfigureAwait(false);
            return Guid.TryParse(answer?["merged_into"]?.GetValue<string>(), out Guid survivor) ? survivor : null;
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

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
    public Task<JsonNode?> SendAsync(HttpMethod method, string path, object? body, CancellationToken cancellationToken = default)
        => SendForAdminAsync(method, path, body, null, cancellationToken);

    /// <summary>Any management API call, optionally for one of your admins (rc.12, staff calls).</summary>
    /// <param name="method">The method.</param>
    /// <param name="path">For example <c>orgs/…/staff</c>.</param>
    /// <param name="body">A JSON body, or none.</param>
    /// <param name="adminAccessToken">The admin's own access token, sent as <c>Sangam-Acting-Admin</c>; none for an ordinary call.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<JsonNode?> SendForAdminAsync(HttpMethod method, string path, object? body, string? adminAccessToken, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(path);
        string token = await GetTokenAsync("sangam.manage", cancellationToken).ConfigureAwait(false);
        using HttpRequestMessage request = new(method, _options.Authority.TrimEnd('/') + "/api/v1/" + path.TrimStart('/'));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (!string.IsNullOrEmpty(adminAccessToken))
        {
            request.Headers.Add(ActingAdminHeader, adminAccessToken);
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body, body.GetType());
        }

        using HttpResponseMessage response = await _http.CreateClient(HttpClientName).SendAsync(request, cancellationToken).ConfigureAwait(false);
        string text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Sangam's management API answered {(int)response.StatusCode} to {method} {path}: {text}", null, response.StatusCode);
        }

        return text.Length == 0 ? null : JsonNode.Parse(text);
    }

    /// <summary>
    /// rc.12 (T1): locks the person's Sangam session when this application's idle timer runs out, with the person's own access
    /// token (from their sign-in, not client credentials). Every application in that session is told (<c>session.locked</c>);
    /// on an enrolled shared ward device the person comes back with their PIN, anywhere else with a full sign-in.
    /// </summary>
    /// <param name="personAccessToken">The access token from the person's sign-in.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><see langword="true"/> when locked; <see langword="false"/> when that sign-in had already ended.</returns>
    public async Task<bool> LockSessionAsync(string personAccessToken, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(personAccessToken);
        using HttpRequestMessage request = new(HttpMethod.Post, _options.Authority.TrimEnd('/') + "/api/v1/sessions/lock");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", personAccessToken);
        using HttpResponseMessage response = await _http.CreateClient(HttpClientName).SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return false;
        }

        if (!response.IsSuccessStatusCode)
        {
            string text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new HttpRequestException($"Sangam answered {(int)response.StatusCode} to the lock: {text}", null, response.StatusCode);
        }

        return true;
    }

    /// <summary>
    /// rc.12 (M12): ends the person's emergency access early, with a reason (the patient left the ward, care handed over, the
    /// emergency is over), with the person's own access token. Tokens refreshed after it no longer carry it.
    /// </summary>
    /// <param name="personAccessToken">The access token from the person's sign-in.</param>
    /// <param name="reason">Why it ends.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><see langword="false"/> when none was in progress.</returns>
    public async Task<bool> EndEmergencyAsync(string personAccessToken, string reason, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(personAccessToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        using HttpRequestMessage request = new(HttpMethod.Post, _options.Authority.TrimEnd('/') + "/api/v1/sessions/emergency/end")
        {
            Content = JsonContent.Create(new Dictionary<string, string> { ["reason"] = reason }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", personAccessToken);
        using HttpResponseMessage response = await _http.CreateClient(HttpClientName).SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return false;
        }

        if (!response.IsSuccessStatusCode)
        {
            string text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new HttpRequestException($"Sangam answered {(int)response.StatusCode} to ending emergency access: {text}", null, response.StatusCode);
        }

        return true;
    }
}
