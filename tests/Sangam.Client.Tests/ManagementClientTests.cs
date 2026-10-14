using System.Net;

namespace Sangam.Client.Tests;

/// <summary>The .NET management client's rc.7 call (where a merged account went) and rc.12 staff calls.</summary>
public sealed class ManagementClientTests
{
    private static readonly Guid Old = Guid.Parse("0192a6b0-0000-7000-8000-0000000000a1");
    private static readonly Guid New = Guid.Parse("0192a6b0-0000-7000-8000-0000000000b2");

    [Fact]
    public async Task MergedInto_GivesTheSurvivingId_AndNullForAnIdThatWasNotMerged()
    {
        using Answering handler = new();
        using SangamManagementClient client = new(new Factory(handler), new SangamOptions { Authority = "https://id.example.in/", ClientId = "app", ClientSecret = "s" });

        Assert.Equal(New, await client.GetMergedIntoAsync(Old));
        Assert.Contains(handler.Seen, s => s == $"GET https://id.example.in/api/v1/users/{Old:D}/merged Bearer t1");

        // A 404 is an answer — not merged, or not an id this application knew — not an error.
        Assert.Null(await client.GetMergedIntoAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task AddingStaff_SendsTheAdminsOwnToken_AndTheDetailsInCamelCase()
    {
        using Answering handler = new();
        using SangamManagementClient client = new(new Factory(handler), new SangamOptions { Authority = "https://id.example.in/", ClientId = "app", ClientSecret = "s" });
        Guid org = Guid.Parse("0192a6b0-0000-7000-8000-0000000000c3");

        await client.AddStaffAsync(org, "admin-token", new SangamStaffAddition(Person: new SangamStaffPerson("Kiran", "Das", "kiran@example.in", null, new DateOnly(1993, 2, 14)), Roles: ["nurse"]));
        Assert.Contains(handler.Seen, s => s == $"POST https://id.example.in/api/v1/orgs/{org:D}/staff Bearer t1");
        Assert.Equal("admin-token", handler.ActingAdmin);
        Assert.Contains("\"person\":{\"firstName\":\"Kiran\"", handler.Body, StringComparison.Ordinal);
        Assert.Contains("\"dateOfBirth\":\"1993-02-14\"", handler.Body, StringComparison.Ordinal);

        // Ordinary calls carry no admin.
        await client.GetMergedIntoAsync(Old);
        Assert.Null(handler.ActingAdmin);
    }

    [Fact]
    public async Task Locking_SendsThePersonsOwnToken_NotClientCredentials()
    {
        using Answering handler = new();
        using SangamManagementClient client = new(new Factory(handler), new SangamOptions { Authority = "https://id.example.in/", ClientId = "app", ClientSecret = "s" });

        Assert.True(await client.LockSessionAsync("person-token"));
        Assert.Equal("POST https://id.example.in/api/v1/sessions/lock Bearer person-token", Assert.Single(handler.Seen));
    }

    [Fact]
    public async Task EndingEmergencyAccess_SendsThePersonsOwnToken_AndTheReason()
    {
        using Answering handler = new();
        using SangamManagementClient client = new(new Factory(handler), new SangamOptions { Authority = "https://id.example.in/", ClientId = "app", ClientSecret = "s" });

        Assert.True(await client.EndEmergencyAsync("person-token", "Care handed over"));
        Assert.Equal("POST https://id.example.in/api/v1/sessions/emergency/end Bearer person-token", Assert.Single(handler.Seen));
        Assert.Contains("\"reason\":\"Care handed over\"", handler.Body, StringComparison.Ordinal);
    }

    private sealed class Answering : HttpMessageHandler
    {
        public List<string> Seen { get; } = [];

        public string? ActingAdmin { get; private set; }

        public string Body { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Seen.Add($"{request.Method} {request.RequestUri} {request.Headers.Authorization}");
            ActingAdmin = request.Headers.TryGetValues(SangamManagementClient.ActingAdminHeader, out IEnumerable<string>? values) ? values.Single() : null;
            Body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            return Answer(request);
        }

        private static HttpResponseMessage Answer(HttpRequestMessage request)
        {
            string path = request.RequestUri!.AbsolutePath;
            if (path == "/connect/token")
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"access_token\":\"t1\",\"expires_in\":3600}") };
            }

            if (path is "/api/v1/sessions/lock" or "/api/v1/sessions/emergency/end")
            {
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            if (path.EndsWith("/staff", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"status\":\"waiting\"}") };
            }

            return path == $"/api/v1/users/{Old:D}/merged"
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($"{{\"merged_into\":\"{New:D}\",\"merged_at\":\"2026-10-12T05:00:00Z\"}}", System.Text.Encoding.UTF8, "application/json") }
                : new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") };
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
