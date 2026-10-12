using System.Net.Sockets;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Sangam.Identity.Server.Tests.Oidc;

/// <summary>
/// OI-058 (ASVS V13.1.1): the identity server on real Kestrel — not the in-memory test server, which skips Kestrel's own
/// request parsing — answers every spelling of a management-API path with 400, 401 or 404, never with the API itself. The
/// requests are written to the socket byte for byte, so no client library tidies the path first. Caddy forwards paths
/// unchanged (the CI job "edge"), so this is what the internet sees.
/// </summary>
[Collection("server")]
public sealed class EdgePathTests
{
    private static readonly string[] Spellings =
    [
        "/api/v1/roles",
        "/api/v1/./roles",
        "/api/v1/x/../roles",
        "/./api/v1/roles",
        "/api/v1/%2e%2e/v1/roles",
        "/api/v1/roles%2F..%2F..%2Fconnect%2Ftoken",
        "/api//v1/roles",
        "/API/V1/Roles",
        "/%61pi/v1/roles",
        "/api/v1/%2561pi",
        "/api/v1/roles%00",
        "/api/v1/roles;x=1",
        "/api/v1/roles%20",
        "/api/v1/roles/",
        "/api\\v1\\roles",
        "/api%5Cv1%5Croles",
    ];

    private readonly SangamServerFactory _factory;

    public EdgePathTests(SangamServerFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task EverySpellingOfAnApiPath_OnRealKestrel_StillNeedsTheToken()
    {
        using WebApplicationFactory<Program> kestrel = _factory.WithWebHostBuilder(_ => { });
        kestrel.UseKestrel(0);
        kestrel.StartServer();
        using HttpClient client = kestrel.CreateClient();
        Uri server = client.BaseAddress!;

        List<string> reached = [];
        foreach (string path in Spellings)
        {
            int status = await RawGetAsync(server, path);
            if (status is not (400 or 401 or 404))
            {
                reached.Add($"{path} answered {status}");
            }
        }

        Assert.Empty(reached);

        // The server really is Kestrel: it removes dot segments before routing (so the API answers 401, asking for the
        // token), and refuses an encoded NUL outright (400).
        Assert.Equal(401, await RawGetAsync(server, "/api/v1/x/../roles"));
        Assert.Equal(400, await RawGetAsync(server, "/api/v1/roles%00"));
    }

    /// <summary>One GET written to the socket as it is, and the status code of the answer.</summary>
    private static async Task<int> RawGetAsync(Uri server, string path)
    {
        using TcpClient tcp = new();
        await tcp.ConnectAsync(server.Host, server.Port);
        await using NetworkStream stream = tcp.GetStream();
        byte[] request = Encoding.ASCII.GetBytes($"GET {path} HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(request);
        using StreamReader reader = new(stream, Encoding.ASCII);
        string statusLine = await reader.ReadLineAsync() ?? string.Empty;
        string[] parts = statusLine.Split(' ');
        return parts.Length > 1 && int.TryParse(parts[1], System.Globalization.CultureInfo.InvariantCulture, out int code) ? code : 0;
    }
}
