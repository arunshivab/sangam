using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Sangam.Identity.Application.Abstractions;
using Sangam.Identity.Application.Security;
using Sangam.Identity.Infrastructure.Policies;

namespace Sangam.Identity.Infrastructure.Tests.Policies;

/// <summary>rc.5 (D-J revised, ASVS V2.1.7): the built-in list, then Pwned Passwords by k-anonymity, falling back on an outage.</summary>
public sealed class PwnedPasswordsCheckerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 6, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task OnlyFiveCharactersOfTheHash_LeaveTheServer_WithPaddingAsked_AndTheMatchIsMadeHere()
    {
        string password = "monsoon tea at baner";
        string hash = Sha1(password);
        using Handler handler = new(_ => (HttpStatusCode.OK, "0000000000000000000000000000000000A:0\n" + hash[5..] + ":42\n"));
        PwnedPasswordsChecker checker = Checker(handler, new Clock(Now));

        Assert.True(await checker.IsBreachedAsync(password));
        Uri asked = Assert.Single(handler.Requests).Uri;
        Assert.Equal("https://api.pwnedpasswords.com/range/" + hash[..5], asked.ToString());
        Assert.DoesNotContain(hash[5..], asked.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.True(handler.Requests[0].Padded);
        Assert.NotNull(checker.Status.LastOnlineSuccess);
        Assert.Null(checker.Status.OnlineFailingSince);
    }

    [Fact]
    public async Task ASuffixListedWithCountZero_IsAPaddingDecoy_NotABreach()
    {
        string password = "monsoon tea at baner";
        using Handler handler = new(_ => (HttpStatusCode.OK, Sha1(password)[5..] + ":0\n"));
        Assert.False(await Checker(handler, new Clock(Now)).IsBreachedAsync(password));
    }

    [Fact]
    public async Task ACommonPassword_IsRefusedFromTheBuiltInList_WithoutAsking()
    {
        using Handler handler = new(_ => throw new InvalidOperationException("must not be called"));
        Assert.True(await Checker(handler, new Clock(Now)).IsBreachedAsync("qwerty123456"));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task AnOutage_NeverBlocks_TheBuiltInListIsTheCheck_AndTheStatusSaysSince()
    {
        Clock clock = new(Now);
        using Handler handler = new(_ => throw new HttpRequestException("no route"));
        PwnedPasswordsChecker checker = Checker(handler, clock);

        Assert.False(await checker.IsBreachedAsync("monsoon tea at baner"));
        Assert.True(await checker.IsBreachedAsync("India@123456"));
        clock.Now = Now.AddMinutes(30);
        Assert.False(await checker.IsBreachedAsync("another long phrase"));

        BreachListStatus status = checker.Status;
        Assert.True(status.Enabled && status.Online);
        Assert.Equal(Now, status.OnlineFailingSince);
        Assert.Equal("The service could not be reached.", status.Problem);
        Assert.Equal(PasswordStrength.CommonPasswordCount, status.FallbackEntries);
    }

    [Fact]
    public async Task AnErrorAnswer_CountsAsAnOutage_AndTheNextAnswerClearsIt()
    {
        HttpStatusCode code = HttpStatusCode.ServiceUnavailable;
        using Handler handler = new(_ => (code, string.Empty));
        PwnedPasswordsChecker checker = Checker(handler, new Clock(Now));

        Assert.False(await checker.IsBreachedAsync("monsoon tea at baner"));
        Assert.Equal("The service answered 503.", checker.Status.Problem);

        code = HttpStatusCode.OK;
        Assert.False(await checker.IsBreachedAsync("monsoon tea at baner"));
        Assert.Null(checker.Status.OnlineFailingSince);
        Assert.Null(checker.Status.Problem);
    }

    [Fact]
    public void Settings_DefaultToOn_ForEveryone_WithTheRangeService_AndAnEmptyEndpointMeansTheListOnly()
    {
        PolicySettings defaults = PolicySettings.From(new ConfigurationBuilder().Build());
        Assert.True(defaults.BreachCheckEnabled);
        Assert.True(defaults.Platform.BreachedPasswordCheck);
        Assert.Equal(PolicySettings.DefaultBreachCheckEndpoint, defaults.BreachCheckEndpoint);
        Assert.Equal(12, defaults.Platform.MinPasswordLength);
        Assert.False(defaults.Platform.RequireCharacterTypes);

        PolicySettings listOnly = PolicySettings.From(Configuration(new() { ["Sangam:Passwords:BreachCheck:Endpoint"] = string.Empty }));
        Assert.Null(listOnly.BreachCheckEndpoint);
        using Handler unused = new(_ => throw new InvalidOperationException());
        Assert.False(Checker(unused, new Clock(Now), listOnly).Status.Online);

        PolicySettings plainHttp = PolicySettings.From(Configuration(new() { ["Sangam:Passwords:BreachCheck:Endpoint"] = "http://example.invalid/range" }));
        Assert.Null(plainHttp.BreachCheckEndpoint);

        PolicySettings off = PolicySettings.From(Configuration(new() { ["Sangam:Passwords:BreachCheck:Enabled"] = "false", ["Sangam:Policy:MinPasswordLength"] = "8" }));
        Assert.False(off.Platform.BreachedPasswordCheck);
        Assert.Equal(12, off.Platform.MinPasswordLength);
    }

    [Fact]
    public async Task SwitchedOff_ChecksNothing()
    {
        PolicySettings off = PolicySettings.From(Configuration(new() { ["Sangam:Passwords:BreachCheck:Enabled"] = "false" }));
        using Handler unused = new(_ => throw new InvalidOperationException());
        Assert.Null(await Checker(unused, new Clock(Now), off).IsBreachedAsync("qwerty123456"));
    }

    private static IConfiguration Configuration(Dictionary<string, string?> values) => new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private static PwnedPasswordsChecker Checker(Handler handler, IClock clock, PolicySettings? settings = null)
        => new(new Factory(handler), settings ?? PolicySettings.From(new ConfigurationBuilder().Build()), clock, NullLogger<PwnedPasswordsChecker>.Instance);

    private static string Sha1(string value)
    {
#pragma warning disable CA5350 // The range service indexes by SHA-1.
        return Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(value)));
#pragma warning restore CA5350
    }

    private sealed class Clock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset Now { get; set; } = now;

        public DateTimeOffset UtcNow => Now;
    }

    private sealed class Handler(Func<HttpRequestMessage, (HttpStatusCode Status, string Body)> answer) : HttpMessageHandler
    {
        public List<(Uri Uri, bool Padded)> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.RequestUri!, request.Headers.TryGetValues("Add-Padding", out IEnumerable<string>? padding) && padding.Contains("true")));
            (HttpStatusCode status, string body) = answer(request);
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
