using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Sangam.Identity.Server.Tests;

/// <summary>The passkey sign-in endpoint and the passkey management page, over HTTP (PR-14, SGM-205).</summary>
[Collection("server")]
public sealed partial class PasskeyPageTests
{
    private readonly SangamServerFactory _factory;

    public PasskeyPageTests(SangamServerFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task TheSignInPage_OffersAPasskey_HiddenUntilTheBrowserSupportsIt()
    {
        using BrowserSession s = new(_factory);
        (HttpStatusCode status, string html) = await s.GetAsync("/login");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains("Sign in with a passkey", html, StringComparison.Ordinal);
        Assert.Contains("data-passkey-signin", html, StringComparison.Ordinal);
        Assert.Contains("/js/passkeys.js", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ThePasskeyEndpoint_RefusesAPostWithoutTheAntiforgeryToken()
    {
        using HttpClient client = NewClient();
        using StringContent body = new("{}", Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await client.PostAsync(new Uri("/login/passkey?handler=Options", UriKind.Relative), body);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ThePasskeyEndpoint_HasNoPageOfItsOwn()
    {
        using HttpClient client = NewClient();
        using HttpResponseMessage response = await client.GetAsync(new Uri("/login/passkey", UriKind.Relative));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }

    [Fact]
    public async Task ManagingPasskeys_NeedsYouToBeSignedIn()
    {
        using HttpClient client = NewClient();
        using HttpResponseMessage response = await client.GetAsync(new Uri("/account/passkeys", UriKind.Relative));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/login", response.Headers.Location!.ToString(), StringComparison.Ordinal);
    }

    [PostgresFact]
    public async Task SignInOptions_RequireUserVerification_AndAForgedAnswerSignsNobodyIn()
    {
        using HttpClient client = NewClient();
        string token = await TokenAsync(client);

        using HttpRequestMessage optionsRequest = new(HttpMethod.Post, new Uri("/login/passkey?handler=Options", UriKind.Relative));
        optionsRequest.Headers.Add("RequestVerificationToken", token);
        optionsRequest.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        using HttpResponseMessage optionsResponse = await client.SendAsync(optionsRequest);
        Assert.Equal(HttpStatusCode.OK, optionsResponse.StatusCode);

        using JsonDocument options = JsonDocument.Parse(await optionsResponse.Content.ReadAsStringAsync());
        string challengeId = options.RootElement.GetProperty("challengeId").GetString()!;
        Assert.True(Guid.TryParse(challengeId, out _));
        JsonElement publicKey = options.RootElement.GetProperty("options");
        Assert.Equal("required", publicKey.GetProperty("userVerification").GetString());
        Assert.False(string.IsNullOrEmpty(publicKey.GetProperty("challenge").GetString()));

        string credential = "{\"id\":\"AAAA\",\"rawId\":\"AAAA\",\"type\":\"public-key\","
            + "\"response\":{\"authenticatorData\":\"AAAA\",\"clientDataJSON\":\"AAAA\",\"signature\":\"AAAA\",\"userHandle\":null},\"clientExtensionResults\":{}}";
        string forged = JsonSerializer.Serialize(new { challengeId, credential });
        using HttpRequestMessage verifyRequest = new(HttpMethod.Post, new Uri("/login/passkey?handler=Verify", UriKind.Relative));
        verifyRequest.Headers.Add("RequestVerificationToken", token);
        verifyRequest.Content = new StringContent(forged, Encoding.UTF8, "application/json");
        using HttpResponseMessage verifyResponse = await client.SendAsync(verifyRequest);
        string verifyBody = await verifyResponse.Content.ReadAsStringAsync();

        Assert.Contains("\"error\"", verifyBody, StringComparison.Ordinal);
        Assert.DoesNotContain("redirect", verifyBody, StringComparison.Ordinal);
        using HttpResponseMessage account = await client.GetAsync(new Uri("/account", UriKind.Relative));
        Assert.Equal(HttpStatusCode.Redirect, account.StatusCode);
    }

    [Fact]
    public async Task AnUnreadableAnswer_IsABadRequest_NotAServerError()
    {
        using HttpClient client = NewClient();
        string token = await TokenAsync(client);
        using HttpRequestMessage request = new(HttpMethod.Post, new Uri("/login/passkey?handler=Verify", UriKind.Relative));
        request.Headers.Add("RequestVerificationToken", token);
        request.Content = new StringContent("not json", Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("could not be read", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    private HttpClient NewClient()
        => _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });

    private static async Task<string> TokenAsync(HttpClient client)
    {
        using HttpResponseMessage page = await client.GetAsync(new Uri("/login", UriKind.Relative));
        Match token = TokenRegex().Match(await page.Content.ReadAsStringAsync());
        Assert.True(token.Success, "No antiforgery token on /login");
        return token.Groups[1].Value;
    }

    [GeneratedRegex("name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"")]
    private static partial Regex TokenRegex();
}
