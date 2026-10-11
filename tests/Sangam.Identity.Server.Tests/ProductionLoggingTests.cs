using System.Text.Json;

namespace Sangam.Identity.Server.Tests;

/// <summary>V-02: no host logs every SQL command in Production; rc.8: structured logs, OpenIddict from Warning.</summary>
public sealed class ProductionLoggingTests
{
    private static readonly string[] Hosts = ["Sangam.Identity.Server", "Sangam.SelfService.Web", "Sangam.Admin.Web", "Sangam.Partner.Web"];

    [Fact]
    public void EveryHost_LogsEfCoreCommandsAtWarning_InItsBaseSettings()
    {
        string root = RepositoryRoot();
        foreach (string host in Hosts)
        {
            using JsonDocument settings = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "src", host, "appsettings.json")));
            string? level = settings.RootElement.GetProperty("Logging").GetProperty("LogLevel").GetProperty("Microsoft.EntityFrameworkCore.Database.Command").GetString();
            Assert.Equal("Warning", level);
        }
    }

    [Fact]
    public void EveryHost_LogsStructuredJson_AndOpenIddictOnlyFromWarning()
    {
        // rc.8 (ASVS V1.7.1, V7.3.1, V7.1.1): one structured format on every host, in which a line break in a logged
        // value is escaped and cannot forge a second entry; OpenIddict's own request logging is not kept at Information.
        string root = RepositoryRoot();
        foreach (string host in Hosts)
        {
            using JsonDocument settings = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "src", host, "appsettings.json")));
            JsonElement logging = settings.RootElement.GetProperty("Logging");
            Assert.Equal("json", logging.GetProperty("Console").GetProperty("FormatterName").GetString());
            Assert.True(logging.GetProperty("Console").GetProperty("FormatterOptions").GetProperty("UseUtcTimestamp").GetBoolean());
            Assert.Equal("Warning", logging.GetProperty("LogLevel").GetProperty("OpenIddict").GetString());
        }
    }

    private static string RepositoryRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Sangam.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Sangam.sln not found above " + AppContext.BaseDirectory);
    }
}
