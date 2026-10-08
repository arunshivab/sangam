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

    /// <summary>The response headers of the last <see cref="PostFormAsync"/>.</summary>
    public IReadOnlyDictionary<string, string> LastPostHeaders { get; private set; } = new Dictionary<string, string>();

    public BrowserSession(WebApplicationFactory<Program> factory)
    {
        _client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
    }

    /// <summary>Follows local redirects (up to ten) and returns where the browser lands.</summary>
    public async Task<(HttpStatusCode Status, string Location, string Html)> FollowAsync(string path)
    {
        string current = path;
        for (int hop = 0; hop < 10; hop++)
        {
            using HttpResponseMessage response = await _client.GetAsync(new Uri(current, UriKind.Relative));
            if (response.StatusCode is not (HttpStatusCode.Found or HttpStatusCode.Redirect or HttpStatusCode.SeeOther))
            {
                return (response.StatusCode, current, await response.Content.ReadAsStringAsync());
            }

            string next = response.Headers.Location!.ToString();
            if (!next.StartsWith('/'))
            {
                return (response.StatusCode, next, string.Empty);
            }

            current = next;
        }

        throw new InvalidOperationException("Too many redirects from " + path);
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

        // Like a browser, submit the form’s hidden bot-check timestamp too (OI-034), unless the test sets it.
        Match stamp = TimestampRegex().Match(page);
        if (stamp.Success)
        {
            form.TryAdd("__form_ts", stamp.Groups[1].Value);
        }

        string target = handler is null ? path : path + (path.Contains('?', StringComparison.Ordinal) ? "&" : "?") + "handler=" + handler;
        using FormUrlEncodedContent content = new(form);
        using HttpResponseMessage response = await _client.PostAsync(new Uri(target, UriKind.Relative), content);
        LastPostHeaders = response.Headers.ToDictionary(h => h.Key, h => string.Join(", ", h.Value), StringComparer.OrdinalIgnoreCase);
        return (response.StatusCode, response.Headers.Location?.ToString(), await response.Content.ReadAsStringAsync());
    }

    /// <summary>Submits a form on a page already in hand (as a browser does), lifting its antiforgery token.</summary>
    public async Task<(HttpStatusCode Status, string? Location, string Html)> SubmitAsync(string path, string page, Dictionary<string, string> fields)
    {
        Match token = TokenRegex().Match(page);
        Assert.True(token.Success, "No antiforgery token on the page for " + path);
        Dictionary<string, string> form = new(fields) { ["__RequestVerificationToken"] = token.Groups[1].Value };
        using FormUrlEncodedContent content = new(form);
        using HttpResponseMessage response = await _client.PostAsync(new Uri(path, UriKind.Relative), content);
        return (response.StatusCode, response.Headers.Location?.ToString(), await response.Content.ReadAsStringAsync());
    }

    public void Dispose() => _client.Dispose();

    [GeneratedRegex("name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"")]
    private static partial Regex TokenRegex();

    [GeneratedRegex("name=\"__form_ts\" value=\"([^\"]+)\"")]
    private static partial Regex TimestampRegex();
}
