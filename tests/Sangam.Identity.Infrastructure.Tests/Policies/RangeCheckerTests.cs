using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Sangam.Identity.Infrastructure.Policies;

namespace Sangam.Identity.Infrastructure.Tests.Policies;

/// <summary>PR-16: the breached-password range lookup sends only a five-character prefix and never blocks on an outage.</summary>
public sealed class RangeCheckerTests
{
    private const string Password = "Correct-Horse-2026!";

    [Fact]
    public async Task OnlyTheFirstFiveHexCharacters_LeaveSangam_AndTheMatchIsMadeHere()
    {
        string hash = Sha1(Password);
        using FakeHandler handler = new(HttpStatusCode.OK, $"0018A45C4D1DEF81644B54AB7F969B88D65:3\r\n{hash[5..].ToLowerInvariant()}:12\r\nFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF:0\r\n");
        RangeBreachedPasswordChecker checker = Checker(handler);

        Assert.True(await checker.IsBreachedAsync(Password));
        Assert.Equal("/range/" + hash[..5], handler.LastRequest!.AbsolutePath);
        Assert.DoesNotContain(hash[5..], handler.LastRequest.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.True(handler.Padded);
    }

    [Fact]
    public async Task AnAbsentSuffix_OrADecoyWithCountZero_IsNotABreach()
    {
        string hash = Sha1(Password);
        using FakeHandler handler = new(HttpStatusCode.OK, $"{hash[5..]}:0\r\n0018A45C4D1DEF81644B54AB7F969B88D65:3\r\n");
        Assert.False(await Checker(handler).IsBreachedAsync(Password));
    }

    [Fact]
    public async Task AnOutage_IsUnknown_NotABlock()
    {
        using FakeHandler failing = new(HttpStatusCode.ServiceUnavailable, string.Empty);
        Assert.Null(await Checker(failing).IsBreachedAsync(Password));
        using FakeHandler throwing = new(HttpStatusCode.OK, string.Empty, fail: true);
        Assert.Null(await Checker(throwing).IsBreachedAsync(Password));
    }

    [Fact]
    public void ParsingTolerates_CaseAndWhitespace()
    {
        Assert.True(RangeBreachedPasswordChecker.Contains(" abcde:2 \n", "ABCDE"));
        Assert.False(RangeBreachedPasswordChecker.Contains("ABCDE:x\n", "ABCDE"));
        Assert.False(RangeBreachedPasswordChecker.Contains(string.Empty, "ABCDE"));
    }

    private static RangeBreachedPasswordChecker Checker(FakeHandler handler)
    {
        PolicySettings settings = PolicySettings.From(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Sangam:Passwords:BreachCheck:Enabled"] = "true",
            ["Sangam:Passwords:BreachCheck:Endpoint"] = "https://breach.example.test/range",
        }).Build());
#pragma warning disable CA2000 // The client does not own the handler; the test disposes it.
        return new RangeBreachedPasswordChecker(new HttpClient(handler, disposeHandler: false), settings, NullLogger<RangeBreachedPasswordChecker>.Instance);
#pragma warning restore CA2000
    }

#pragma warning disable CA5350 // The range service is keyed by SHA-1; this mirrors it.
    private static string Sha1(string text) => Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(text)));
#pragma warning restore CA5350

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;
        private readonly bool _fail;

        public FakeHandler(HttpStatusCode status, string body, bool fail = false)
        {
            _status = status;
            _body = body;
            _fail = fail;
        }

        public Uri? LastRequest { get; private set; }

        public bool Padded { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request.RequestUri;
            Padded = request.Headers.TryGetValues("Add-Padding", out IEnumerable<string>? values) && values.Contains("true");
            if (_fail)
            {
                throw new HttpRequestException("down");
            }

            return Task.FromResult(new HttpResponseMessage(_status) { Content = new StringContent(_body) });
        }
    }
}
