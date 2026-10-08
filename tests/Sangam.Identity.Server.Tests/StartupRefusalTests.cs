using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Sangam.Identity.Infrastructure;

namespace Sangam.Identity.Server.Tests;

/// <summary>V-07 and V-08: a refused start exits cleanly with a reason; HTTPS redirection only where the app is exposed directly.</summary>
public sealed class StartupRefusalTests
{
    [Fact]
    public async Task ARefusedStart_LogsTheReasonAtCritical_AndExitsWithCode1()
    {
        string server = typeof(Program).Assembly.Location;
        string host = Environment.ProcessPath is string path && Path.GetFileNameWithoutExtension(path).Equals("dotnet", StringComparison.OrdinalIgnoreCase) ? path : "dotnet";
        ProcessStartInfo start = new(host, [server])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(server)!,
        };
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        start.Environment["ASPNETCORE_HTTP_PORTS"] = "0";
        start.Environment["ConnectionStrings__Sangam"] = "Host=localhost;Database=refused;Username=nobody;Password=none";
        start.Environment["Sangam__Anjal__BaseUrl"] = "https://anjal.example.invalid/";
        start.Environment["Sangam__Anjal__ApiKey"] = "anjal-test-key-0123456789";

        using Process process = Process.Start(start)!;
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> errors = process.StandardError.ReadToEndAsync();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(90));
        await process.WaitForExitAsync(timeout.Token);
        string log = await output + await errors;

        Assert.Equal(StartupGuard.RefusedExitCode, process.ExitCode);
        Assert.Contains("crit: Sangam.Startup", log, StringComparison.Ordinal);
        Assert.Contains("No key-ring certificate is configured", log, StringComparison.Ordinal);
        Assert.DoesNotContain("Unhandled exception", log, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Production", null, true)]
    [InlineData("Production", "172.30.0.0/24", false)]
    [InlineData("Development", null, false)]
    [InlineData("Testing", null, false)]
    public void HttpsRedirection_IsLeftToTheProxy_WhenOneIsTrusted(string environment, string? knownNetworks, bool expected)
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [WebHosting.KnownNetworksKey] = knownNetworks,
        }).Build();
        Assert.Equal(expected, WebHosting.ShouldRedirectToHttps(environment, configuration));
    }
}
