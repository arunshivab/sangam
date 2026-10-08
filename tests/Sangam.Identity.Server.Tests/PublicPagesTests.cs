using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Sangam.Identity.Server.Authentication;

namespace Sangam.Identity.Server.Tests;

/// <summary>DEF-025 and REQ-089: the footer's and the registration form's documents exist, and grievances have a channel.</summary>
[Collection("server")]
public sealed class PublicPagesTests
{
    private readonly SangamServerFactory _factory;

    public PublicPagesTests(SangamServerFactory factory)
    {
        _factory = factory;
    }

    [Theory]
    [InlineData("/terms")]
    [InlineData("/privacy")]
    [InlineData("/help")]
    [InlineData("/privacy/grievance")]
    public async Task EveryPublicPage_AnswersWithoutSigningIn_AndLinksToGrievances(string path)
    {
        using HttpClient client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using HttpResponseMessage response = await client.GetAsync(new Uri(path, UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("href=\"/privacy/grievance\"", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Terms_SayTheyArePending_UntilAFileIsConfigured_ThenShowIt()
    {
        using HttpClient pending = _factory.CreateClient();
        Assert.Contains("being reviewed by counsel", await pending.GetStringAsync(new Uri("/terms", UriKind.Relative)), StringComparison.Ordinal);

        string file = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(file, "First paragraph of the terms.\n\nSecond paragraph.");
            using HttpClient published = _factory.WithWebHostBuilder(b => b.UseSetting(LegalDocuments.TermsPathKey, file)).CreateClient();
            string html = await published.GetStringAsync(new Uri("/terms", UriKind.Relative));
            Assert.Contains("First paragraph of the terms.", html, StringComparison.Ordinal);
            Assert.Contains("Second paragraph.", html, StringComparison.Ordinal);
            Assert.DoesNotContain("being reviewed by counsel", html, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task Grievance_NamesTheOfficerAndTheTimes_FromD_D_AndShowsOnlyWhatIsConfigured()
    {
        using HttpClient decided = _factory.CreateClient();
        string page = System.Net.WebUtility.HtmlDecode(await decided.GetStringAsync(new Uri("/privacy/grievance", UriKind.Relative)));
        Assert.Contains("Arun Shiva Balasubramanian", page, StringComparison.Ordinal);
        Assert.Contains("mailto:grievance@sangamid.in", page, StringComparison.Ordinal);
        Assert.Contains("We acknowledge every grievance within 2 working days and resolve it within 30 days of receiving it. Every grievance is logged.", page, StringComparison.Ordinal);

        using HttpClient unset = _factory.WithWebHostBuilder(b =>
        {
            b.UseSetting(LegalDocuments.OfficerNameKey, string.Empty);
            b.UseSetting(LegalDocuments.AcknowledgeDaysKey, string.Empty);
            b.UseSetting(LegalDocuments.ResponseDaysKey, string.Empty);
        }).CreateClient();
        string before = await unset.GetStringAsync(new Uri("/privacy/grievance", UriKind.Relative));
        Assert.Contains("To be named before Sangam opens", before, StringComparison.Ordinal);
        Assert.DoesNotContain("We will answer within", before, StringComparison.Ordinal);
        Assert.DoesNotContain("We acknowledge every grievance", before, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheTermsAndPrivacyPlaceholders_NameTheOwnerAndJurisdiction()
    {
        using HttpClient client = _factory.CreateClient();
        string terms = System.Net.WebUtility.HtmlDecode(await client.GetStringAsync(new Uri("/terms", UriKind.Relative)));
        string privacy = System.Net.WebUtility.HtmlDecode(await client.GetStringAsync(new Uri("/privacy", UriKind.Relative)));
        Assert.Contains("Owner and operator: Dr. Arun Shiva Balasubramanian. Jurisdiction: Ahmedabad.", terms, StringComparison.Ordinal);
        Assert.Contains("data fiduciary under the Digital Personal Data Protection Act, 2023: Dr. Arun Shiva Balasubramanian.", privacy, StringComparison.Ordinal);
        Assert.DoesNotContain("imagiQa Healthcare", terms + privacy, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnInvitation_AsksTheVisitorToSignIn_AndComesBackAfterwards()
    {
        using HttpClient client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using HttpResponseMessage response = await client.GetAsync(new Uri("/invite/abc123", UriKind.Relative));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        string location = response.Headers.Location!.ToString();
        Assert.Contains("/login", location, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(Uri.EscapeDataString("/invite/abc123"), location, StringComparison.OrdinalIgnoreCase);
    }
}
