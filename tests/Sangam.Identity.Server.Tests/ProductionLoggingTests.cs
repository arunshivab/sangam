using System.Text.Json;

namespace Sangam.Identity.Server.Tests;

/// <summary>V-02: no host logs every SQL command in Production.</summary>
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
