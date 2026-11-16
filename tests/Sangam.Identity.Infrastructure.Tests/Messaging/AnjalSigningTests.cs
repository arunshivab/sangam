using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Sangam.Identity.Infrastructure.Messaging;

namespace Sangam.Identity.Infrastructure.Tests.Messaging;

/// <summary>
/// rc.17: M1 (b) request signing exactly as Anjal verifies it, M2 (Anjal's 409 "still sending" is waited for) and
/// M7 (a) the health check with the key, which never leaves for another host.
/// </summary>
public sealed class AnjalSigningTests
{
    private const string Key = "ak_live_0123456789abcdef0123456789abcdef";
    private static readonly DateTimeOffset Start = new(2026, 11, 15, 6, 30, 0, TimeSpan.Zero);

    [Fact]
    public void TheSigningString_IsFourLines_WithNoTrailingNewline()
    {
        byte[] body = Encoding.UTF8.GetBytes("{\"a\":1}");
        string text = AnjalRequestSigner.SigningString("post", "/api/v1/messages/email", "1794724200", body);

        string hash = Convert.ToHexStringLower(SHA256.HashData(body));
        Assert.Equal("POST\n/api/v1/messages/email\n1794724200\n" + hash, text);
        Assert.False(text.EndsWith('\n'));
    }

    [Fact]
    public void AnEmptyBody_IsHashedAsTheEmptyString()
    {
        string text = AnjalRequestSigner.SigningString("GET", "/api/v1/health", "1", ReadOnlySpan<byte>.Empty);
        Assert.EndsWith("\ne3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheKeyId_IsSkAndTwelveHexOfTheSpkiHash()
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using AnjalRequestSigner signer = AnjalRequestSigner.FromPem(key.ExportPkcs8PrivateKeyPem());

        string expected = "sk_" + Convert.ToHexStringLower(SHA256.HashData(key.ExportSubjectPublicKeyInfo()))[..12];
        Assert.Equal(expected, signer.KeyId);
        Assert.Equal(15, signer.KeyId.Length);
    }

    [Fact]
    public void A_Sec1PemKey_IsAccepted_ButOnlyP256()
    {
        using ECDsa p256 = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using AnjalRequestSigner sec1 = AnjalRequestSigner.FromPem(p256.ExportECPrivateKeyPem());
        Assert.StartsWith("sk_", sec1.KeyId, StringComparison.Ordinal);

        using ECDsa p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        Assert.Throws<InvalidOperationException>(() => AnjalRequestSigner.FromPem(p384.ExportPkcs8PrivateKeyPem()));
        Assert.Throws<InvalidOperationException>(() => AnjalRequestSigner.FromPem("-----BEGIN PRIVATE KEY-----\nnot a key\n-----END PRIVATE KEY-----"));
        Assert.Throws<InvalidOperationException>(() => AnjalRequestSigner.FromPem(p256.ExportSubjectPublicKeyInfoPem()));
    }

    [Fact]
    public async Task ASignedEmail_CarriesTheKeyAndThreeHeaders_ThatAnjalCanVerify()
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using AnjalRequestSigner signer = AnjalRequestSigner.FromPem(key.ExportPkcs8PrivateKeyPem());
        using Recorder anjal = new(HttpStatusCode.Accepted);
        AnjalClient client = Client(anjal, signer);

        AnjalResult result = await client.PostAsync("api/v1/messages/email", new { subject = "Sign-in code", text = "ಕನ್ನಡ text" }, 1, "k***@example.com");

        Assert.True(result.Accepted);
        Seen request = Assert.Single(anjal.Requests);
        Assert.Equal("Bearer " + Key, request.Headers["Authorization"]);
        Assert.Equal(signer.KeyId, request.Headers[AnjalRequestSigner.KeyIdHeader]);
        Assert.Equal(Start.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture), request.Headers[AnjalRequestSigner.TimestampHeader]);
        Assert.True(Verifies(key, request), "the signature must verify over method, target, timestamp and the exact body bytes");
        Assert.Equal(64, FromBase64Url(request.Headers[AnjalRequestSigner.SignatureHeader]).Length);
        Assert.DoesNotContain('=', request.Headers[AnjalRequestSigner.SignatureHeader]);
    }

    [Fact]
    public async Task EveryRetry_IsSignedAfresh_OverTheSameBody_AndKeepsItsIdempotencyKey()
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using AnjalRequestSigner signer = AnjalRequestSigner.FromPem(key.ExportPkcs8PrivateKeyPem());
        using Recorder anjal = new(HttpStatusCode.ServiceUnavailable, HttpStatusCode.Conflict, HttpStatusCode.Accepted);
        AnjalClient client = Client(anjal, signer);

        AnjalResult result = await client.PostAsync("api/v1/messages/email", new { text = "123456" }, 5, "k***@example.com");

        Assert.True(result.Accepted);
        Assert.Equal(3, anjal.Requests.Count);
        Assert.Single(anjal.Requests.Select(r => Convert.ToBase64String(r.Body)).Distinct());
        Assert.Single(anjal.Requests.Select(r => r.Headers[AnjalClient.IdempotencyHeader]).Distinct());
        Assert.Equal(3, anjal.Requests.Select(r => r.Headers[AnjalRequestSigner.SignatureHeader]).Distinct().Count());
        Assert.Equal(3, anjal.Requests.Select(r => r.Headers[AnjalRequestSigner.TimestampHeader]).Distinct().Count());
        Assert.All(anjal.Requests, r => Assert.True(Verifies(key, r)));
    }

    [Fact]
    public async Task AnjalsStillSending409_IsWaitedFor_SoTheMessageIsNotCountedAsFailed()
    {
        using Recorder anjal = new(HttpStatusCode.Conflict, HttpStatusCode.Accepted);
        AnjalResult result = await Client(anjal, null).PostAsync("api/v1/messages/email", new { text = "x" }, 5, "k***@example.com");
        Assert.True(result.Accepted);
        Assert.Equal(2, result.Attempts);
    }

    [Fact]
    public async Task ASignatureRefusal_IsFinal()
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using AnjalRequestSigner signer = AnjalRequestSigner.FromPem(key.ExportPkcs8PrivateKeyPem());
        using Recorder anjal = new(HttpStatusCode.Unauthorized, HttpStatusCode.Accepted);
        AnjalResult result = await Client(anjal, signer).PostAsync("api/v1/messages/email", new { text = "x" }, 5, "k***@example.com");
        Assert.False(result.Accepted);
        Assert.Equal("HTTP 401", result.Error);
        Assert.Single(anjal.Requests);
    }

    [Fact]
    public async Task WithoutASigningKey_OnlyTheApiKeyIsSent()
    {
        using Recorder anjal = new(HttpStatusCode.Accepted);
        AnjalClient client = Client(anjal, null);
        await client.PostAsync("api/v1/messages/email", new { text = "x" }, 1, "k***@example.com");
        Seen request = Assert.Single(anjal.Requests);
        Assert.Equal("Bearer " + Key, request.Headers["Authorization"]);
        Assert.False(request.Headers.ContainsKey(AnjalRequestSigner.SignatureHeader));
        Assert.Null(client.SigningKeyId);
    }

    [Fact]
    public async Task TheHealthCheck_SendsTheKeyAndASignature_ToAnjalOnly()
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using AnjalRequestSigner signer = AnjalRequestSigner.FromPem(key.ExportPkcs8PrivateKeyPem());
        using Recorder anjal = new(HttpStatusCode.OK, HttpStatusCode.ServiceUnavailable, HttpStatusCode.OK);
        AnjalClient client = Client(anjal, signer);

        Assert.True(await client.CheckHealthAsync(new Uri("https://mail.anjal.example.in/api/v1/health")));
        Assert.False(await client.CheckHealthAsync(new Uri("https://mail.anjal.example.in/api/v1/health")));
        Assert.True(await client.CheckHealthAsync(new Uri("https://status.example.org/health")));

        Seen first = anjal.Requests[0];
        Assert.Equal("GET", first.Method);
        Assert.Equal("Bearer " + Key, first.Headers["Authorization"]);
        Assert.True(Verifies(key, first));
        Seen elsewhere = anjal.Requests[2];
        Assert.False(elsewhere.Headers.ContainsKey("Authorization"));
        Assert.False(elsewhere.Headers.ContainsKey(AnjalRequestSigner.SignatureHeader));
    }

    [Fact]
    public async Task TheSignature_CoversAPathPrefixAndAQuery_AsWrittenOutByHand()
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using AnjalRequestSigner signer = AnjalRequestSigner.FromPem(key.ExportPkcs8PrivateKeyPem());
        using Recorder anjal = new(HttpStatusCode.Accepted);
        AnjalClient client = Client(anjal, signer, "https://mail.anjal.example.in/gateway/");

        await client.PostAsync("api/v1/messages/email?trace=1", new { text = "x" }, 1, "k***@example.com");

        // Written out from Anjal's spec, not with Sangam's own SigningString: method, raw target, timestamp, body hash.
        Seen request = Assert.Single(anjal.Requests);
        Assert.Equal("/gateway/api/v1/messages/email?trace=1", request.Target);
        string expected = "POST\n/gateway/api/v1/messages/email?trace=1\n1794724200\n" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("{\"text\":\"x\"}")));
        Assert.Equal("1794724200", request.Headers[AnjalRequestSigner.TimestampHeader]);
        Assert.Equal("{\"text\":\"x\"}", Encoding.UTF8.GetString(request.Body));
        Assert.True(key.VerifyData(Encoding.UTF8.GetBytes(expected), FromBase64Url(request.Headers[AnjalRequestSigner.SignatureHeader]), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
    }

    [Fact]
    public async Task AnjalsRetryAfter_OnA409_IsWhatSangamWaits()
    {
        using Recorder anjal = new(HttpStatusCode.Conflict, HttpStatusCode.Accepted) { RetryAfterSeconds = 2 };
        List<TimeSpan> waits = [];
        AnjalClient client = Client(anjal, null);
        client.Delay = (wait, _) =>
        {
            waits.Add(wait);
            return Task.CompletedTask;
        };

        AnjalResult result = await client.PostAsync("api/v1/messages/email", new { text = "x" }, 5, "k***@example.com");

        Assert.True(result.Accepted);
        Assert.Equal(TimeSpan.FromSeconds(2), Assert.Single(waits));
    }

    [Theory]
    [InlineData("https://mail.anjal.example.in:8443/api/v1/health")]
    [InlineData("http://mail.anjal.example.in/api/v1/health")]
    [InlineData("https://anjal.example.in/api/v1/health")]
    public async Task TheKey_IsNotSent_ToAnotherPortSchemeOrHost(string url)
    {
        using Recorder anjal = new(HttpStatusCode.OK);
        await Client(anjal, null).CheckHealthAsync(new Uri(url));
        Assert.False(Assert.Single(anjal.Requests).Headers.ContainsKey("Authorization"));
    }

    [Fact]
    public async Task WithASigningKeySetting_TheHostsClientSigns_AndAWrongKeyStopsTheHost()
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        ServiceCollection signed = Host(key.ExportPkcs8PrivateKeyPem());
        await using (ServiceProvider provider = signed.BuildServiceProvider())
        {
            AnjalClient client = provider.GetRequiredService<AnjalClient>();
            Assert.Equal(AnjalRequestSigner.KeyIdOf(key), client.SigningKeyId);

            // The client is made when the host starts, so its key id is logged at start-up, not at the first message.
            Type startup = Assert.Single(signed, d => d.ServiceType.Name == "AnjalStartup").ServiceType;
            Assert.NotNull(provider.GetRequiredService(startup));
        }

        await using (ServiceProvider unsigned = Host(null).BuildServiceProvider())
        {
            Assert.Null(unsigned.GetRequiredService<AnjalClient>().SigningKeyId);
        }

        using ECDsa p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        Assert.Throws<InvalidOperationException>(() => Host(p384.ExportPkcs8PrivateKeyPem()));
    }

    private static ServiceCollection Host(string? signingKey)
    {
        Dictionary<string, string?> settings = new()
        {
            ["ConnectionStrings:Sangam"] = "Host=localhost;Database=unused;Username=unused;Password=unused",
            ["Sangam:Email:UseOutbox"] = "true",
            ["Sangam:Anjal:BaseUrl"] = "https://mail.anjal.example.in/",
            ["Sangam:Anjal:ApiKey"] = Key,
            ["Sangam:Anjal:SigningKey"] = signingKey,
        };
        ServiceCollection services = new();
        services.AddLogging();
        services.AddSangamInfrastructure(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
        return services;
    }

    private static AnjalClient Client(Recorder anjal, AnjalRequestSigner? signer, string baseUrl = "https://mail.anjal.example.in/")
    {
        AnjalOptions options = new() { BaseUrl = baseUrl, ApiKey = Key, RetryDelay = TimeSpan.FromMilliseconds(1) };
        DateTimeOffset now = Start;
#pragma warning disable CA2000 // The client lives as long as the test; the handler is disposed by the test.
        return new AnjalClient(new HttpClient(anjal) { BaseAddress = new Uri(options.BaseUrl) }, options, NullLogger<AnjalClient>.Instance, signer)
        {
            Delay = (_, _) => Task.CompletedTask,
            Now = () =>
            {
                DateTimeOffset stamp = now;
                now = now.AddSeconds(2);
                return stamp;
            },
        };
#pragma warning restore CA2000
    }

    /// <summary>Verifies a request the way Anjal does, from what actually arrived.</summary>
    private static bool Verifies(ECDsa key, Seen request)
    {
        string text = AnjalRequestSigner.SigningString(request.Method, request.Target, request.Headers[AnjalRequestSigner.TimestampHeader], request.Body);
        byte[] signature = FromBase64Url(request.Headers[AnjalRequestSigner.SignatureHeader]);
        return key.VerifyData(Encoding.UTF8.GetBytes(text), signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }

    private static byte[] FromBase64Url(string text)
    {
        string padded = text.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(padded.PadRight(padded.Length + ((4 - (padded.Length % 4)) % 4), '='));
    }

    private sealed record Seen(string Method, string Target, Dictionary<string, string> Headers, byte[] Body);

    /// <summary>Answers with each status in turn (the last repeats) and keeps the exact bytes that arrived.</summary>
    private sealed class Recorder(params HttpStatusCode[] answers) : HttpMessageHandler
    {
        public List<Seen> Requests { get; } = [];

        public int? RetryAfterSeconds { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Dictionary<string, string> headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
            byte[] body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            Requests.Add(new Seen(request.Method.Method, request.RequestUri!.PathAndQuery, headers, body));
            HttpStatusCode status = answers[Math.Min(Requests.Count - 1, answers.Length - 1)];
            HttpResponseMessage response = new(status)
            {
                Content = new StringContent((int)status < 300 ? "{\"id\":\"msg-1\",\"status\":\"queued\"}" : "{\"code\":\"x\"}", Encoding.UTF8, "application/json"),
            };
            if (RetryAfterSeconds is int seconds && (int)status >= 400)
            {
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(seconds));
            }

            return response;
        }
    }
}
