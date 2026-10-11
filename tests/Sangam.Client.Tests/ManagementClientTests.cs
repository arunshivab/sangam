using System.Net;

namespace Sangam.Client.Tests;

/// <summary>The .NET management client's rc.7 call: where a merged account went.</summary>
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

    private sealed class Answering : HttpMessageHandler
    {
        public List<string> Seen { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Seen.Add($"{request.Method} {request.RequestUri} {request.Headers.Authorization}");
            string path = request.RequestUri!.AbsolutePath;
            if (path == "/connect/token")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"access_token\":\"t1\",\"expires_in\":3600}") });
            }

            return Task.FromResult(path == $"/api/v1/users/{Old:D}/merged"
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($"{{\"merged_into\":\"{New:D}\",\"merged_at\":\"2026-10-12T05:00:00Z\"}}", System.Text.Encoding.UTF8, "application/json") }
                : new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") });
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
