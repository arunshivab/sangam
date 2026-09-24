using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Sangam.Identity.Server.Tests;

/// <summary>
/// A cookie-keeping client that submits Razor Pages forms the way a browser with JavaScript
/// off does: fetch the page, lift the antiforgery token, post the fields, follow nothing.
/// </summary>
internal sealed partial class BrowserSession : IDisposable
{
    private readonly HttpClient _client;

    public BrowserSession(WebApplicationFactory<Program> factory)
    {
        _client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
    }

    public async Task<(HttpStatusCode Status, string Html)> GetAsync(string path)
    {
        using HttpResponseMessage response = await _client.GetAsync(new Uri(path, UriKind.Relative));
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    public async Task<(HttpStatusCode Status, string? Location, string Html)> PostFormAsync(string path, Dictionary<string, string> fields, string? handler = null)
    {
        (HttpStatusCode getStatus, string page) = await GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, getStatus);
        Match token = TokenRegex().Match(page);
        Assert.True(token.Success, "No antiforgery token on " + path);

        Dictionary<string, string> form = new(fields) { ["__RequestVerificationToken"] = token.Groups[1].Value };
        string target = handler is null ? path : path + (path.Contains('?', StringComparison.Ordinal) ? "&" : "?") + "handler=" + handler;
        using FormUrlEncodedContent content = new(form);
        using HttpResponseMessage response = await _client.PostAsync(new Uri(target, UriKind.Relative), content);
        return (response.StatusCode, response.Headers.Location?.ToString(), await response.Content.ReadAsStringAsync());
    }

    public void Dispose() => _client.Dispose();

    [GeneratedRegex("name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"")]
    private static partial Regex TokenRegex();
}
