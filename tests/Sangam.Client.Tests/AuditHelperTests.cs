using System.Net;
using System.Security.Claims;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Sangam.Client.Audit;

namespace Sangam.Client.Tests;

/// <summary>R6: the audit helper fills in the person and the request, refuses a bad event, buffers, and forwards.</summary>
public sealed class AuditHelperTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sangam-audit-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public async Task TheRecorder_FillsInThePersonAndTheRequest_AndBuffersAValidEvent()
    {
        SangamAuditOptions options = new() { AppId = "lims", AppVersion = "1.2.0", Environment = "development", BufferPath = Path.Combine(_dir, "pending.jsonl") };
        DefaultHttpContext context = new();
        context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "0192a6b0-0000-7000-8000-000000000042"), new Claim("acr", "urn:sangam:acr:sign"), new Claim("sid", "s-1")], "test"));
        context.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.10");
        context.Request.Headers.UserAgent = "tests";
        SangamAuditRecorder recorder = new(new HttpContextAccessor { HttpContext = context }, Options.Create(options));

        JsonObject e = await recorder.RecordAsync(new SangamAuditEntry("lims.result.sign", "sign", "result", "R-1") { Signature = ("sig-1", "Approved", "sha256:x") });
        Assert.Empty(SangamAudit.Validate(e));
        Assert.Equal("203.0.113.10", e["client"]!["ip"]!.GetValue<string>());
        Assert.Equal("urn:sangam:acr:sign", e["actor"]!["acr"]!.GetValue<string>());
        Assert.Single(await File.ReadAllLinesAsync(options.BufferPath));
        Assert.Equal((byte)'{', (await File.ReadAllBytesAsync(options.BufferPath))[0]);

        // A sign event without its signature breaks the schema and is refused, not buffered.
        await Assert.ThrowsAsync<ArgumentException>(() => recorder.RecordAsync(new SangamAuditEntry("lims.result.sign", "sign", "result", "R-2")));
        Assert.Single(await File.ReadAllLinesAsync(options.BufferPath));
    }

    [Fact]
    public async Task TheForwarder_SendsBatches_WithAnAuditToken_AndKeepsThemWhenRefused()
    {
        SangamAuditOptions options = new() { AppId = "lims", BufferPath = Path.Combine(_dir, "pending.jsonl"), Endpoint = "https://audit.example.in/v1/events" };
        DefaultHttpContext context = new();
        context.Connection.RemoteIpAddress = IPAddress.Loopback;
        SangamAuditRecorder recorder = new(new HttpContextAccessor { HttpContext = context }, Options.Create(options));
        for (int i = 0; i < 3; i++)
        {
            await recorder.RecordAsync(new SangamAuditEntry("lims.job.run", "admin", "job", "J-" + i) { ActorType = "system" });
        }

        using Recording handler = new();
        using SangamManagementClient tokens = new(new Factory(handler), new SangamOptions { Authority = "https://id.example.in", ClientId = "lims", ClientSecret = "s" });
        using SangamAuditForwarder forwarder = new(Options.Create(options), tokens, new Factory(handler), NullLogger<SangamAuditForwarder>.Instance);

        handler.Refuse = true;
        Assert.Equal(0, await forwarder.FlushAsync());
        Assert.Equal(3, (await File.ReadAllLinesAsync(options.BufferPath)).Length);

        handler.Refuse = false;
        Assert.Equal(3, await forwarder.FlushAsync());
        Assert.Empty(await File.ReadAllLinesAsync(options.BufferPath));
        Assert.Contains(handler.Seen, s => s.Contains("scope=audit.write", StringComparison.Ordinal));
        Assert.Contains(handler.Seen, s => s.StartsWith("POST https://audit.example.in/v1/events Bearer t1", StringComparison.Ordinal) && s.Contains("\"J-2\"", StringComparison.Ordinal));
    }

    private sealed class Recording : HttpMessageHandler
    {
        public bool Refuse { get; set; }

        public List<string> Seen { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            Seen.Add($"{request.Method} {request.RequestUri} {request.Headers.Authorization} {body}");
            if (request.RequestUri!.AbsolutePath == "/connect/token")
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"access_token\":\"t1\",\"expires_in\":3600}") };
            }

            return new HttpResponseMessage(Refuse ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.Accepted);
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
