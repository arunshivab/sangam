using System.Net;
using System.Text.RegularExpressions;
using Sangam.Identity.Domain.Enums;

namespace Sangam.Partner.Web.Tests;

/// <summary>
/// PR-32 (CAP-110): the evidence pack download repeats the console's gate — an antiforgery token, an authenticator,
/// and administering the application — and answers with the zip.
/// </summary>
[Collection("partner-db")]
public sealed partial class EvidenceDownloadTests : IClassFixture<PartnerFactory>
{
    private readonly PartnerFactory _factory;

    public EvidenceDownloadTests(PartnerFactory factory)
    {
        _factory = factory;
    }

    [PostgresFact]
    public async Task AnAdministrator_WithAnAuthenticator_DownloadsTheZip()
    {
        Guid app = await _factory.SeedAppAsync("Evidence LIMS");
        Guid owner = await _factory.SeedUserAsync(mfa: true, "Owner");
        await _factory.MakeAdminAsync(app, owner, AppAdminRole.Owner);
        using HttpClient client = _factory.ClientFor(owner);

        string token = await TokenAsync(client, app);
        using HttpResponseMessage response = await PostAsync(client, app, token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/zip", response.Content.Headers.ContentType?.MediaType);
        Assert.StartsWith("sangam-evidence-", response.Content.Headers.ContentDisposition?.FileName?.Trim('"'), StringComparison.Ordinal);
        byte[] zip = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal((byte)'P', zip[0]);
        Assert.Equal((byte)'K', zip[1]);
    }

    [PostgresFact]
    public async Task WithoutTheAntiforgeryToken_NothingIsBuilt()
    {
        Guid app = await _factory.SeedAppAsync("Evidence LIMS");
        Guid owner = await _factory.SeedUserAsync(mfa: true, "Owner");
        await _factory.MakeAdminAsync(app, owner, AppAdminRole.Owner);
        using HttpClient client = _factory.ClientFor(owner);

        using HttpResponseMessage response = await PostAsync(client, app, token: null);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [PostgresFact]
    public async Task SomeoneWhoDoesNotAdministerIt_OrHasNoAuthenticator_GetsNoPack()
    {
        Guid app = await _factory.SeedAppAsync("Evidence LIMS");
        Guid theirs = await _factory.SeedAppAsync("Another app");
        Guid stranger = await _factory.SeedUserAsync(mfa: true, "Other");
        Guid plain = await _factory.SeedUserAsync(mfa: true, "Plain");
        await _factory.MakeAdminAsync(theirs, stranger, AppAdminRole.Owner);
        await _factory.MakeAdminAsync(app, plain, AppAdminRole.Admin);

        // A genuine token for each (from a page they may see), then the request for this application.
        using HttpClient strangerClient = _factory.ClientFor(stranger);
        string strangerToken = await TokenAsync(strangerClient, theirs);
        using HttpResponseMessage notTheirs = await PostAsync(strangerClient, app, strangerToken);
        Assert.Equal(HttpStatusCode.NotFound, notTheirs.StatusCode);

        using HttpClient plainClient = _factory.ClientFor(plain);
        string plainToken = await TokenAsync(plainClient, app);
        await _factory.SetTwoFactorAsync(plain, enabled: false);
        using HttpResponseMessage noAuthenticator = await PostAsync(plainClient, app, plainToken);
        Assert.Equal(HttpStatusCode.Forbidden, noAuthenticator.StatusCode);
    }

    [PostgresFact]
    public async Task OneAdministrator_CanBuildOnlyAFewPacksAtATime()
    {
        // R7 (ASVS V11.1.4): six in ten minutes, then 429.
        Guid app = await _factory.SeedAppAsync("Evidence LIMS");
        Guid owner = await _factory.SeedUserAsync(mfa: true, "Busy");
        await _factory.MakeAdminAsync(app, owner, AppAdminRole.Owner);
        using HttpClient client = _factory.ClientFor(owner);
        string token = await TokenAsync(client, app);

        for (int i = 0; i < 6; i++)
        {
            using HttpResponseMessage allowed = await PostAsync(client, app, token);
            Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        }

        using HttpResponseMessage refused = await PostAsync(client, app, token);
        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
    }

    /// <summary>The antiforgery token the evidence tab gives this client.</summary>
    private static async Task<string> TokenAsync(HttpClient client, Guid app)
    {
        string html = await client.GetStringAsync(new Uri($"/apps/{app:D}/evidence", UriKind.Relative));
        Assert.Contains($"action=\"/apps/{app:D}/evidence\"", html, StringComparison.Ordinal);
        return Token().Match(html).Groups[1].Value;
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, Guid app, string? token)
    {
        Dictionary<string, string> fields = new()
        {
            ["from"] = DateTime.UtcNow.AddDays(-30).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            ["to"] = DateTime.UtcNow.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
        };
        if (token is not null)
        {
            fields["__RequestVerificationToken"] = token;
        }

        using FormUrlEncodedContent content = new(fields);
        return await client.PostAsync(new Uri($"/apps/{app:D}/evidence", UriKind.Relative), content);
    }

    [GeneratedRegex("name=\"__RequestVerificationToken\" value=\"([^\"]+)\"")]
    private static partial Regex Token();
}
