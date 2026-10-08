using Microsoft.AspNetCore.Mvc.Testing;
using Sangam.Identity.Server.Authentication;

namespace Sangam.Identity.Server.Tests;

/// <summary>OI-034: the honeypot and the signed form timestamp refuse bots; people are unaffected.</summary>
[Collection("server")]
public sealed class AntibotTests
{
    private readonly SangamServerFactory _factory;

    public AntibotTests(SangamServerFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Forgot_WithTheHoneypotFilled_IsRefused()
    {
        using BrowserSession s = new(_factory);
        (_, _, string html) = await s.PostFormAsync("/forgot", new() { ["Email"] = "kaveri@example.com", [Antibot.HoneypotField] = "https://spam.example" });
        Assert.Contains(Antibot.Refusal, html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Forgot_WithoutTheTimestamp_IsRefused()
    {
        using BrowserSession s = new(_factory);
        (_, _, string html) = await s.PostFormAsync("/forgot", new() { ["Email"] = "kaveri@example.com", [Antibot.TimestampField] = string.Empty });
        Assert.Contains(Antibot.Refusal, html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Forgot_PostedFasterThanAPersonCould_IsRefused()
    {
        WebApplicationFactory<Program> strict = _factory.WithWebHostBuilder(b => b.UseSetting(Antibot.MinimumSecondsKey, "30"));
        using BrowserSession s = new(strict);
        (_, _, string html) = await s.PostFormAsync("/forgot", new() { ["Email"] = "kaveri@example.com" });
        Assert.Contains(Antibot.Refusal, html, StringComparison.Ordinal);
    }

    [PostgresFact]
    public async Task Forgot_AnOrdinaryPost_IsAccepted()
    {
        using BrowserSession s = new(_factory);
        (_, _, string html) = await s.PostFormAsync("/forgot", new() { ["Email"] = "nobody@example.com" });
        Assert.DoesNotContain(Antibot.Refusal, html, StringComparison.Ordinal);
    }

    [Fact]
    public void Check_RefusesATamperedToken()
    {
        Microsoft.AspNetCore.DataProtection.IDataProtectionProvider provider = new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider();
        Assert.Equal("invalid", Antibot.Check(provider, null, "not-a-token", DateTimeOffset.UtcNow, TimeSpan.Zero));
        Assert.Null(Antibot.Check(provider, null, Antibot.CreateToken(provider, DateTimeOffset.UtcNow.AddSeconds(-5)), DateTimeOffset.UtcNow, TimeSpan.FromSeconds(2)));
    }
}
