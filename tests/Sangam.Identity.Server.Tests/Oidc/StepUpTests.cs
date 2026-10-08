using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Web;
using Microsoft.Extensions.DependencyInjection;
using Sangam.Identity.Domain;
using Sangam.Identity.Infrastructure.Seeding;
using Sangam.Identity.Infrastructure.Services;

namespace Sangam.Identity.Server.Tests.Oidc;

/// <summary>PR-17 (SGM-207): applications ask for a level (acr_values) and freshness (max_age); tokens say what was done.</summary>
[Collection("server")]
public sealed partial class StepUpTests
{
    private const string Password = "Correct-Horse-2026!";
    private readonly SangamServerFactory _factory;

    public StepUpTests(SangamServerFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Discovery_AdvertisesTheLevelsAndTheClaims()
    {
        using HttpClient client = _factory.CreateClient();
        JsonElement doc = JsonDocument.Parse(await client.GetStringAsync(new Uri("/.well-known/openid-configuration", UriKind.Relative))).RootElement;
        string[] acr = [.. doc.GetProperty("acr_values_supported").EnumerateArray().Select(e => e.GetString()!)];
        Assert.Equal(AuthenticationAssurance.Supported, acr);
        string[] claims = [.. doc.GetProperty("claims_supported").EnumerateArray().Select(e => e.GetString()!)];
        Assert.Contains("acr", claims);
        Assert.Contains("amr", claims);
        Assert.Contains("auth_time", claims);
    }

    [PostgresFact]
    public async Task WithoutAStepUp_TheIdToken_StillSaysHowThePersonSignedIn()
    {
        using BrowserSession s = new(_factory);
        await RegisterAsync(s);
        (string verifier, string challenge) = Pkce();
        JsonElement idToken = await ConsentAndExchangeAsync(s, Authorize(challenge), verifier);

        Assert.Equal(AuthenticationAssurance.Acr1, idToken.GetProperty("acr").GetString());
        Assert.Equal(["pwd"], Methods(idToken));
        Assert.True(idToken.GetProperty("auth_time").GetInt64() > DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeSeconds());
    }

    [PostgresFact]
    public async Task AskingForLevel2_SendsAPasswordSessionBackToSignIn_WhichAddsTheCodeStep()
    {
        using BrowserSession s = new(_factory);
        string email = await RegisterAsync(s);
        (string verifier, string challenge) = Pkce();
        string authorize = Authorize(challenge) + "&acr_values=" + Uri.EscapeDataString(AuthenticationAssurance.Acr2);

        (_, string location, _) = await s.FollowAsync(authorize);
        Assert.Equal("/login", location.Split('?')[0]);

        (_, string? afterPassword, _) = await s.PostFormAsync("/login?returnUrl=" + Uri.EscapeDataString(authorize), new Dictionary<string, string> { ["Email"] = email, ["Password"] = Password });
        Assert.Equal("/login/verify", afterPassword?.Split('?')[0]);
        string code = CodeRegex().Match(_factory.Services.GetRequiredService<InMemoryEmailOutbox>().LatestFor(email)!.Message.TextBody).Value;
        (_, string? afterCode, _) = await s.PostFormAsync(afterPassword!, new Dictionary<string, string> { ["Code"] = code });
        Assert.StartsWith("/connect/authorize", afterCode, StringComparison.Ordinal);

        JsonElement idToken = await ConsentAndExchangeAsync(s, afterCode!, verifier);
        Assert.Equal(AuthenticationAssurance.Acr2, idToken.GetProperty("acr").GetString());
        Assert.Equal(["pwd", "otp", "mfa"], Methods(idToken));
    }

    [PostgresFact]
    public async Task MaxAge_AsksForAFreshSignIn_AndPromptNoneGetsAnError()
    {
        using BrowserSession s = new(_factory);
        await RegisterAsync(s);
        (_, string challenge) = Pkce();

        (_, string silent, _) = await s.FollowAsync(Authorize(challenge) + "&max_age=0&prompt=none");
        Assert.StartsWith(DevelopmentSeeder.SampleRedirectUri, silent, StringComparison.Ordinal);
        Assert.Contains("error=login_required", silent, StringComparison.Ordinal);

        (_, string location, _) = await s.FollowAsync(Authorize(challenge) + "&max_age=0");
        Assert.Equal("/login", location.Split('?')[0]);
        Assert.DoesNotContain("max_age", HttpUtility.UrlDecode(location), StringComparison.Ordinal);
    }

    [PostgresFact]
    public async Task AskingForAPasskey_HidesThePasswordForm()
    {
        using BrowserSession s = new(_factory);
        string email = await RegisterAsync(s);
        (_, string challenge) = Pkce();
        string authorize = Authorize(challenge) + "&acr_values=" + Uri.EscapeDataString(AuthenticationAssurance.Acr3);

        (_, string location, string html) = await s.FollowAsync(authorize);
        Assert.Equal("/login", location.Split('?')[0]);
        Assert.Contains("Sign in with a passkey", html, StringComparison.Ordinal);
        Assert.DoesNotContain("name=\"Password\"", html, StringComparison.Ordinal);
        Assert.Contains("@", email, StringComparison.Ordinal);
    }

    private static string[] Methods(JsonElement token)
        => token.GetProperty("amr").ValueKind == JsonValueKind.Array
            ? [.. token.GetProperty("amr").EnumerateArray().Select(e => e.GetString()!)]
            : [token.GetProperty("amr").GetString()!];

    private async Task<JsonElement> ConsentAndExchangeAsync(BrowserSession s, string authorize, string verifier)
    {
        (_, string path, _) = await s.FollowAsync(authorize);
        string redirect = path;
        if (path.StartsWith("/consent", StringComparison.Ordinal))
        {
            (_, string? allow, _) = await s.PostFormAsync(path, [], handler: "Allow");
            (_, redirect, _) = allow!.StartsWith('/') ? await s.FollowAsync(allow) : (HttpStatusCode.Found, allow, string.Empty);
        }

        string code = HttpUtility.ParseQueryString(new Uri(redirect).Query)["code"]!;
        using HttpClient api = _factory.CreateClient();
        using FormUrlEncodedContent form = new(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = DevelopmentSeeder.SampleRedirectUri,
            ["client_id"] = DevelopmentSeeder.SampleClientId,
            ["client_secret"] = DevelopmentSeeder.SampleClientSecret,
            ["code_verifier"] = verifier,
        });
        using HttpResponseMessage response = await api.PostAsync(new Uri("/connect/token", UriKind.Relative), form);
        string body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, body);
        string jwt = JsonDocument.Parse(body).RootElement.GetProperty("id_token").GetString()!;
        string payload = jwt.Split('.')[1].Replace('-', '+').Replace('_', '/');
        payload = payload.PadRight(payload.Length + ((4 - (payload.Length % 4)) % 4), '=');
        return JsonDocument.Parse(Convert.FromBase64String(payload)).RootElement;
    }

    private static string Authorize(string challenge)
        => "/connect/authorize?client_id=" + DevelopmentSeeder.SampleClientId
        + "&redirect_uri=" + Uri.EscapeDataString(DevelopmentSeeder.SampleRedirectUri)
        + "&response_type=code&scope=" + Uri.EscapeDataString("openid profile email")
        + "&state=xyz&code_challenge=" + challenge + "&code_challenge_method=S256";

    private static (string Verifier, string Challenge) Pkce()
    {
        string verifier = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        string challenge = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return (verifier, challenge);
    }

    private async Task<string> RegisterAsync(BrowserSession s)
    {
        string email = $"stepup-{Guid.NewGuid():N}@example.in";
        (HttpStatusCode st, string? loc, string html) = await s.PostFormAsync("/register", new Dictionary<string, string>
        {
            ["FirstName"] = "Anil",
            ["LastName"] = "Kumar",
            ["Email"] = email,
            ["Country"] = "IN",
            ["MobileNumber"] = Random.Shared.NextInt64(7000000000, 9999999999).ToString(CultureInfo.InvariantCulture),
            ["BirthDay"] = "21",
            ["BirthMonth"] = "9",
            ["BirthYear"] = "1980",
            ["Gender"] = "male",
            ["Password"] = Password,
            ["AcceptTerms"] = "true",
        });
        Assert.True(st == HttpStatusCode.Found && loc == "/verify", html);
        string code = CodeRegex().Match(_factory.Services.GetRequiredService<InMemoryEmailOutbox>().LatestFor(email)!.Message.TextBody).Value;
        (_, string? ok, _) = await s.PostFormAsync("/verify", new Dictionary<string, string> { ["Code"] = code });
        Assert.Equal("/verified", ok);
        return email;
    }

    [GeneratedRegex("[0-9]{6}")]
    private static partial Regex CodeRegex();
}
