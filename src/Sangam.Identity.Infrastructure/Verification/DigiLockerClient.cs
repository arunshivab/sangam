using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Sangam.Identity.Application.Verification;
using Sangam.Identity.Domain.Enums;

namespace Sangam.Identity.Infrastructure.Verification;

/// <summary>
/// DigiLocker's OAuth 2.0 authorisation-code flow with PKCE (S256), as its Requester API Specification describes: send the
/// person to the authorisation endpoint, exchange the code (with the client secret and the verifier) at the token endpoint,
/// and read <c>digilockerid</c>, <c>name</c>, <c>dob</c> (DDMMYYYY) and <c>gender</c> (M, F, T) from the token response, or
/// from the user-details endpoint when the response leaves them out. Nothing else is read: no documents, no e-Aadhaar,
/// no Aadhaar number. The access token is used once and dropped.
/// </summary>
public sealed class DigiLockerClient
{
    private readonly IHttpClientFactory _http;
    private readonly DigiLockerSettings _settings;

    /// <summary>Initialises the client.</summary>
    /// <param name="http">HTTP client factory.</param>
    /// <param name="settings">Settings.</param>
    public DigiLockerClient(IHttpClientFactory http, DigiLockerSettings settings)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    /// <summary>Whether people may verify with DigiLocker.</summary>
    public bool Enabled => _settings.Enabled;

    /// <summary>A fresh PKCE verifier (43 characters) and its S256 challenge.</summary>
    public static (string Verifier, string Challenge) NewPkce()
    {
        string verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        return (verifier, Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))));
    }

    /// <summary>The address to send the person to.</summary>
    /// <param name="state">The state to get back.</param>
    /// <param name="challenge">The PKCE challenge.</param>
    /// <param name="redirectUri">Where DigiLocker sends the person back to.</param>
    public Uri AuthorizeUri(string state, string challenge, string redirectUri)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(challenge);
        ArgumentNullException.ThrowIfNull(redirectUri);
        string query = string.Join('&', new Dictionary<string, string>
        {
            ["response_type"] = "code",
            ["client_id"] = _settings.ClientId,
            ["redirect_uri"] = redirectUri,
            ["state"] = state,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
            ["purpose"] = "verification",
        }.Select(p => p.Key + "=" + Uri.EscapeDataString(p.Value)));
        return new Uri(_settings.AuthorizeUrl + (_settings.AuthorizeUrl.Contains('?', StringComparison.Ordinal) ? "&" : "?") + query);
    }

    /// <summary>Exchanges a code for the person's verified identity; <see langword="null"/> when DigiLocker refuses or answers something unusable.</summary>
    /// <param name="code">The authorisation code.</param>
    /// <param name="verifier">The PKCE verifier.</param>
    /// <param name="redirectUri">The redirect address used to get the code.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<VerifiedIdentity?> ExchangeAsync(string code, string verifier, string redirectUri, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(code);
        ArgumentNullException.ThrowIfNull(verifier);
        ArgumentNullException.ThrowIfNull(redirectUri);
        HttpClient client = _http.CreateClient(DigiLockerSettings.ClientName);
        using FormUrlEncodedContent form = new(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["client_id"] = _settings.ClientId,
            ["client_secret"] = _settings.ClientSecret,
            ["redirect_uri"] = redirectUri,
            ["code_verifier"] = verifier,
        });
        using HttpResponseMessage response = await client.PostAsync(new Uri(_settings.TokenUrl), form, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        using JsonDocument token = await ReadAsync(response, cancellationToken).ConfigureAwait(false);
        if (token.RootElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        VerifiedIdentity? identity = Parse(token.RootElement);
        if (identity is not null || Text(token.RootElement, "access_token") is not string accessToken)
        {
            return identity;
        }

        using HttpRequestMessage request = new(HttpMethod.Get, _settings.UserUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using HttpResponseMessage user = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!user.IsSuccessStatusCode)
        {
            return null;
        }

        using JsonDocument details = await ReadAsync(user, cancellationToken).ConfigureAwait(false);
        return details.RootElement.ValueKind == JsonValueKind.Object ? Parse(details.RootElement) : null;
    }

    /// <summary>Reads an identity from a DigiLocker response, or <see langword="null"/> when a part is missing or malformed.</summary>
    /// <param name="root">The JSON object.</param>
    public static VerifiedIdentity? Parse(JsonElement root)
    {
        string? subject = Text(root, "digilockerid");
        string? name = Text(root, "name")?.Trim();
        string? dob = Text(root, "dob");
        string? gender = Text(root, "gender");
        if (string.IsNullOrWhiteSpace(subject) || string.IsNullOrWhiteSpace(name) || name.Length > 200
            || !DateOnly.TryParseExact(dob, "ddMMyyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly born))
        {
            return null;
        }

        Gender? parsed = gender?.Trim().ToUpperInvariant() switch
        {
            "M" => Gender.Male,
            "F" => Gender.Female,
            "T" => Gender.Other,
            _ => null,
        };
        return parsed is Gender g ? new VerifiedIdentity("digilocker", subject.Trim(), name, born, g) : null;
    }

    private static async Task<JsonDocument> ReadAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return JsonDocument.Parse(body.Length > 64_000 ? "null" : body);
        }
        catch (JsonException)
        {
            return JsonDocument.Parse("null");
        }
    }

    private static string? Text(JsonElement root, string name)
        => root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
