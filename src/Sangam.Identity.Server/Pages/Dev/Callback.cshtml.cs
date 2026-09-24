using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Sangam.Identity.Infrastructure.Seeding;

namespace Sangam.Identity.Server.Pages.Dev;

/// <summary>
/// Development-only stand-in for a partner application's redirect URI, so the authorization
/// code flow can be walked end to end in a browser before <c>Sangam.Client</c> and the sample
/// app exist (PR-07). Shows the code, exchanges it for tokens on demand and prints the claims.
/// Returns 404 outside Development.
/// </summary>
public sealed class CallbackModel : PageModel
{
    // Relaxed encoding so "+91…" reads as itself rather than \u002B; this page is for eyes, not machines.
    private static readonly JsonSerializerOptions Indented = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly IWebHostEnvironment _environment;
    private readonly IHttpClientFactory _httpClientFactory;

    /// <summary>Initialises the page.</summary>
    /// <param name="environment">Host environment.</param>
    /// <param name="httpClientFactory">Client factory for the back-channel calls.</param>
    public CallbackModel(IWebHostEnvironment environment, IHttpClientFactory httpClientFactory)
    {
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
    }

    /// <summary>The authorization code Sangam returned.</summary>
    [BindProperty(SupportsGet = true)]
    public string? Code { get; set; }

    /// <summary>The state the app sent.</summary>
    [BindProperty(SupportsGet = true)]
    public string? State { get; set; }

    /// <summary>Error code, when the user declined or the request was refused.</summary>
    [BindProperty(SupportsGet = true, Name = "error")]
    public string? Error { get; set; }

    /// <summary>Human-readable error description.</summary>
    [BindProperty(SupportsGet = true, Name = "error_description")]
    public string? ErrorDescription { get; set; }

    /// <summary>Whether the exchange has run.</summary>
    public bool Exchanged { get; private set; }

    /// <summary>Error from the exchange, if it failed.</summary>
    public string? ExchangeError { get; private set; }

    /// <summary>Token type from the token response.</summary>
    public string TokenType { get; private set; } = string.Empty;

    /// <summary>Access-token lifetime in seconds.</summary>
    public string ExpiresIn { get; private set; } = string.Empty;

    /// <summary>Granted scopes.</summary>
    public string Scope { get; private set; } = string.Empty;

    /// <summary>Whether a refresh token was issued.</summary>
    public bool HasRefreshToken { get; private set; }

    /// <summary>Pretty-printed ID token payload.</summary>
    public string IdTokenClaims { get; private set; } = string.Empty;

    /// <summary>Pretty-printed userinfo response.</summary>
    public string UserInfo { get; private set; } = string.Empty;

    /// <summary>Shows the code, or 404 outside Development.</summary>
    public IActionResult OnGet() => _environment.IsDevelopment() ? Page() : NotFound();

    /// <summary>Exchanges the code for tokens and calls userinfo, as a partner app would.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        if (!_environment.IsDevelopment())
        {
            return NotFound();
        }

        if (string.IsNullOrEmpty(Code))
        {
            ExchangeError = "There is no code to exchange.";
            return Page();
        }

        string origin = $"{Request.Scheme}://{Request.Host}";
        using HttpClient client = _httpClientFactory.CreateClient();
        using FormUrlEncodedContent form = new(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = Code,
            ["redirect_uri"] = origin + "/dev/callback",
            ["client_id"] = DevelopmentSeeder.SampleClientId,
            ["client_secret"] = DevelopmentSeeder.SampleClientSecret,
            ["code_verifier"] = DevelopmentSeeder.DevCallbackVerifier,
        });

        using HttpResponseMessage response = await client.PostAsync(new Uri(origin + "/connect/token"), form, cancellationToken);
        string body = await response.Content.ReadAsStringAsync(cancellationToken);
        JsonElement json = JsonDocument.Parse(body).RootElement;

        if (!response.IsSuccessStatusCode)
        {
            ExchangeError = json.TryGetProperty("error_description", out JsonElement description)
                ? description.GetString()
                : "The token endpoint refused the exchange: " + body;
            return Page();
        }

        Exchanged = true;
        TokenType = json.GetProperty("token_type").GetString() ?? string.Empty;
        ExpiresIn = json.GetProperty("expires_in").GetInt64().ToString(CultureInfo.InvariantCulture);
        Scope = json.TryGetProperty("scope", out JsonElement scope) ? scope.GetString() ?? string.Empty : string.Empty;
        HasRefreshToken = json.TryGetProperty("refresh_token", out _);
        IdTokenClaims = json.TryGetProperty("id_token", out JsonElement idToken) ? Pretty(Payload(idToken.GetString()!)) : "(no id_token)";

        string accessToken = json.GetProperty("access_token").GetString()!;
        using HttpRequestMessage me = new(HttpMethod.Get, origin + "/connect/userinfo");
        me.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using HttpResponseMessage meResponse = await client.SendAsync(me, cancellationToken);
        UserInfo = Pretty(await meResponse.Content.ReadAsStringAsync(cancellationToken));
        return Page();
    }

    private static string Payload(string jwt)
    {
        string payload = jwt.Split('.')[1].Replace('-', '+').Replace('_', '/');
        payload = payload.PadRight(payload.Length + ((4 - (payload.Length % 4)) % 4), '=');
        return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(payload));
    }

    private static string Pretty(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            return JsonSerializer.Serialize(document.RootElement, Indented);
        }
        catch (JsonException)
        {
            return json;
        }
    }
}
