using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Sangam.Identity.Application.Abstractions;
using Sangam.Identity.Application.Security;
using Sangam.Identity.Infrastructure.Policies;

namespace Sangam.Identity.Infrastructure.Tests.Policies;

/// <summary>D-J: the breached-password check is offline only — a compact local list with a fast prefix lookup.</summary>
public sealed class OfflineBreachListTests : IDisposable
{
    private static readonly string[] Breached = ["password123", "Correct-Horse-2026!", "letmein", "Sangam@2026"];
    private readonly string _dir = Directory.CreateTempSubdirectory("sangam-pwned-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void ASingleFileDownload_IsImported_AndEveryBreachedPasswordIsFound()
    {
        string source = Path.Combine(_dir, "pwnedpasswords.txt");
        File.WriteAllLines(source, Lines(Breached.Concat(Fillers(5000)), withPrefix: true));
        string list = Path.Combine(_dir, "pwned.bin");

        long count = PwnedPasswordList.Build(source, list, new DateOnly(2026, 10, 1));

        Assert.Equal(5004, count);
        (PwnedPasswordList.Header? header, string? problem) = PwnedPasswordList.ReadHeader(list);
        Assert.Null(problem);
        Assert.Equal(new DateOnly(2026, 10, 1), header!.ListDate);
        foreach (string password in Breached)
        {
            Assert.True(PwnedPasswordList.Contains(list, header, PwnedPasswordList.Key(password)), password);
        }

        Assert.False(PwnedPasswordList.Contains(list, header, PwnedPasswordList.Key("A-Long-Unique-Passphrase-Nobody-Uses-42")));
    }

    [Fact]
    public void TheDownloadersDirectoryOfPrefixFiles_IsImportedToo_AndRareHashesCanBeLeftOut()
    {
        string source = Path.Combine(_dir, "ranges");
        Directory.CreateDirectory(source);
        foreach (IGrouping<string, string> range in Breached.Select(Sha1).GroupBy(h => h[..5]))
        {
            File.WriteAllLines(Path.Combine(source, range.Key + ".txt"), range.Select(h => h[5..] + (h == Sha1("letmein") ? ":1" : ":40")));
        }

        string list = Path.Combine(_dir, "pwned.bin");
        Assert.Equal(3, PwnedPasswordList.Build(source, list, new DateOnly(2026, 10, 1), minimumCount: 2));
        PwnedPasswordList.Header header = PwnedPasswordList.ReadHeader(list).Header!;
        Assert.True(PwnedPasswordList.Contains(list, header, PwnedPasswordList.Key("password123")));
        Assert.False(PwnedPasswordList.Contains(list, header, PwnedPasswordList.Key("letmein")));
    }

    [Fact]
    public void AListOutOfHashOrder_IsRefused()
    {
        string source = Path.Combine(_dir, "unsorted.txt");
        File.WriteAllLines(source, Lines(Breached, withPrefix: true).Reverse());
        Assert.Throws<InvalidDataException>(() => PwnedPasswordList.Build(source, Path.Combine(_dir, "pwned.bin"), new DateOnly(2026, 10, 1)));
    }

    [Fact]
    public async Task TheCheck_IsOffUntilTheListIsLoaded_ThenFindsBreaches_AndSaysWhy()
    {
        string list = Path.Combine(_dir, "pwned.bin");
        FakeClock clock = new();
        OfflineBreachedPasswordChecker checker = Checker(enabled: true, list, clock);

        Assert.False(checker.Available);
        Assert.Null(await checker.IsBreachedAsync("password123"));
        BreachListStatus missing = checker.Status;
        Assert.True(missing.Enabled);
        Assert.False(missing.Loaded);
        Assert.Contains("not there", missing.Problem, StringComparison.Ordinal);

        string source = Path.Combine(_dir, "pwnedpasswords.txt");
        File.WriteAllLines(source, Lines(Breached, withPrefix: true));
        PwnedPasswordList.Build(source, list, new DateOnly(2026, 10, 1));
        clock.UtcNow += TimeSpan.FromMinutes(2);

        Assert.True(checker.Available);
        Assert.True(await checker.IsBreachedAsync("Sangam@2026"));
        Assert.False(await checker.IsBreachedAsync("A-Long-Unique-Passphrase-Nobody-Uses-42"));
        Assert.Equal(new BreachListStatus(true, true, new DateOnly(2026, 10, 1), 4, null), checker.Status);
    }

    [Fact]
    public async Task SwitchedOff_NothingIsChecked_EvenWithAList()
    {
        string list = Path.Combine(_dir, "pwned.bin");
        string source = Path.Combine(_dir, "pwnedpasswords.txt");
        File.WriteAllLines(source, Lines(Breached, withPrefix: true));
        PwnedPasswordList.Build(source, list, new DateOnly(2026, 10, 1));

        OfflineBreachedPasswordChecker checker = Checker(enabled: false, list, new FakeClock());
        Assert.False(checker.Available);
        Assert.Null(await checker.IsBreachedAsync("password123"));
        Assert.False(checker.Status.Enabled);
        Assert.True(checker.Status.Loaded);
    }

    [Fact]
    public async Task TheTool_ImportsAndReports()
    {
        string source = Path.Combine(_dir, "pwnedpasswords.txt");
        File.WriteAllLines(source, Lines(Breached, withPrefix: true));
        string list = Path.Combine(_dir, "pwned.bin");
        using StringWriter output = new();

        Assert.Equal(0, await BreachListCommand.RunAsync(["import", "--source", source, "--output", list, "--date", "2026-10-01"], output));
        Assert.Equal(0, await BreachListCommand.RunAsync(["status", "--list", list], output));
        Assert.Equal(2, await BreachListCommand.RunAsync(["import", "--source", source], output));
        Assert.Contains("list dated 2026-10-01", output.ToString(), StringComparison.Ordinal);
    }

    private static OfflineBreachedPasswordChecker Checker(bool enabled, string list, IClock clock)
    {
        IConfiguration config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Sangam:Passwords:BreachCheck:Enabled"] = enabled ? "true" : "false",
            ["Sangam:Passwords:BreachCheck:ListPath"] = list,
        }).Build();
        return new OfflineBreachedPasswordChecker(PolicySettings.From(config), clock, NullLogger<OfflineBreachedPasswordChecker>.Instance);
    }

    private static IEnumerable<string> Fillers(int count) => Enumerable.Range(0, count).Select(i => "filler-" + i);

    private static IEnumerable<string> Lines(IEnumerable<string> passwords, bool withPrefix)
        => passwords.Select(Sha1).Order(StringComparer.Ordinal).Select(h => (withPrefix ? h : h[5..]) + ":7");

    private static string Sha1(string password)
    {
#pragma warning disable CA5350 // The list is SHA-1 by definition.
        return Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(password)));
#pragma warning restore CA5350
    }

    private sealed class FakeClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = new(2026, 10, 7, 9, 0, 0, TimeSpan.Zero);
    }
}
