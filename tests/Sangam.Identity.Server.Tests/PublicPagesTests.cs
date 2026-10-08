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
    public async Task Grievance_ShowsOnlyWhatIsConfigured()
    {
        using HttpClient unset = _factory.CreateClient();
        string before = await unset.GetStringAsync(new Uri("/privacy/grievance", UriKind.Relative));
        Assert.Contains("To be named before Sangam opens", before, StringComparison.Ordinal);
        Assert.DoesNotContain("We will answer within", before, StringComparison.Ordinal);

        using HttpClient set = _factory.WithWebHostBuilder(b =>
        {
            b.UseSetting(LegalDocuments.OfficerNameKey, "Grievance Officer, imagiQa");
            b.UseSetting(LegalDocuments.ResponseDaysKey, "15");
        }).CreateClient();
        string after = await set.GetStringAsync(new Uri("/privacy/grievance", UriKind.Relative));
        Assert.Contains("Grievance Officer, imagiQa", after, StringComparison.Ordinal);
        Assert.Contains("We will answer within 15 days.", after, StringComparison.Ordinal);
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
