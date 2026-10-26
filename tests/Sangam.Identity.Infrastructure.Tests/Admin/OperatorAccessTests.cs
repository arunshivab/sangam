using System.Text.RegularExpressions;
using Sangam.Identity.Domain;
using Sangam.Identity.Domain.Enums;

namespace Sangam.Identity.Infrastructure.Tests.Admin;

/// <summary>
/// rc.16 (ASVS V1.4.4, V1.4.5): one mechanism checks every operator action, by named right. A public method that acts
/// for an operator (it takes <c>Guid operatorUserId</c>) and does not reach <c>OperatorAccess</c> fails the build here,
/// so a new console feature cannot forget its check.
/// </summary>
public sealed partial class OperatorAccessTests
{
    // Returns the operator's rank to the console (to show or hide its own buttons) and grants nothing.
    private static readonly HashSet<string> Exempt = new(StringComparer.Ordinal) { "GetRoleAsync" };

    [Fact]
    public void EveryOperatorMethod_ChecksThroughOperatorAccess()
    {
        string root = Path.Combine(RepoRoot(), "src", "Sangam.Identity.Infrastructure");
        List<string> missing = [];
        int checkedMethods = 0;
        foreach (string file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !f.EndsWith($"{Path.DirectorySeparatorChar}OperatorAccess.cs", StringComparison.Ordinal)))
        {
            string source = File.ReadAllText(file);
            Dictionary<string, List<string>> bodies = Bodies(source);
            foreach (Match m in Declaration().Matches(source))
            {
                if (!m.Groups["parameters"].Value.Contains("Guid operatorUserId", StringComparison.Ordinal) || Body(source, m.Index + m.Length) is not string body)
                {
                    continue;
                }

                checkedMethods++;
                if (!Exempt.Contains(m.Groups["name"].Value) && !Reaches(body, bodies, depth: 0))
                {
                    missing.Add($"{Path.GetRelativePath(root, file)}: {m.Groups["name"].Value}");
                }
            }
        }

        Assert.True(checkedMethods >= 30, $"Only {checkedMethods} operator methods found; the scan is not finding them.");
        Assert.True(missing.Count == 0, "Operator methods that do not check through OperatorAccess:\n" + string.Join('\n', missing));
    }

    [Fact]
    public void EveryRight_IsGivenByARank_AndRanksStayCumulative()
    {
        foreach (OperatorRight right in Enum.GetValues<OperatorRight>())
        {
            PlatformRole lowest = OperatorRights.LowestRank(right);
            Assert.All(PlatformRanks.All, rank => Assert.Equal(rank >= lowest, OperatorRights.Allows(rank, right)));
            Assert.False(OperatorRights.Allows(null, right));
        }

        Assert.Equal(Enum.GetValues<OperatorRight>().Length, OperatorRights.For(PlatformRole.Owner).Count);
        Assert.Equal([OperatorRight.UseConsole, OperatorRight.SearchUsers, OperatorRight.OpenUserRecord, OperatorRight.ListApplications, OperatorRight.ReadGrievances, OperatorRight.ViewMonitoring], OperatorRights.For(PlatformRole.Viewer));
    }

    [Fact]
    public void TheAuthorityModel_NamesEveryRight()
    {
        string doc = File.ReadAllText(Path.Combine(RepoRoot(), "docs", "authority-model.md"));
        List<string> absent = [.. Enum.GetNames<OperatorRight>().Where(n => !doc.Contains($"`{n}`", StringComparison.Ordinal))];
        Assert.True(absent.Count == 0, "docs/authority-model.md does not name: " + string.Join(", ", absent));
    }

    /// <summary>Whether a body checks through OperatorAccess itself or through a method of the same file that does.</summary>
    private static bool Reaches(string body, Dictionary<string, List<string>> bodies, int depth)
    {
        // A check that decides: a refusal (RequireAsync), a yes or no (HasAsync), or the rank read and then compared
        // through OperatorRights.Allows. Reading the rank alone does not count.
        bool decides = body.Contains("_access.RequireAsync(", StringComparison.Ordinal)
            || body.Contains("_access.HasAsync(", StringComparison.Ordinal)
            || ((body.Contains("_access.RankAsync(", StringComparison.Ordinal) || body.Contains("OperatorAccess.RankAsync(", StringComparison.Ordinal) || body.Contains("RoleAsync(", StringComparison.Ordinal))
                && body.Contains("OperatorRights.Allows(", StringComparison.Ordinal));
        if (decides)
        {
            return true;
        }

        if (depth >= 3)
        {
            return false;
        }

        foreach (Match call in Call().Matches(body))
        {
            if (bodies.TryGetValue(call.Groups["name"].Value, out List<string>? callees) && callees.Any(c => Reaches(c, bodies, depth + 1)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Every method body in a file, by method name (overloads share a name).</summary>
    private static Dictionary<string, List<string>> Bodies(string source)
    {
        Dictionary<string, List<string>> bodies = new(StringComparer.Ordinal);
        foreach (Match m in AnyDeclaration().Matches(source))
        {
            if (Body(source, m.Index + m.Length) is string body)
            {
                if (!bodies.TryGetValue(m.Groups["name"].Value, out List<string>? list))
                {
                    bodies[m.Groups["name"].Value] = list = [];
                }

                list.Add(body);
            }
        }

        return bodies;
    }

    /// <summary>The body after a declaration's parameter list: a braced block, or an expression up to its semicolon; null for none.</summary>
    private static string? Body(string source, int from)
    {
        int i = from;
        while (i < source.Length && source[i] != '{' && source[i] != ';' && !(source[i] == '=' && i + 1 < source.Length && source[i + 1] == '>'))
        {
            i++;
        }

        if (i >= source.Length || source[i] == ';')
        {
            return null;
        }

        char open = source[i] == '{' ? '{' : '(';
        int depth = 0;
        for (int j = i; j < source.Length; j++)
        {
            char c = source[j];
            if (c is '{' or '(')
            {
                depth++;
            }
            else if (c is '}' or ')')
            {
                depth--;
                if (open == '{' && depth == 0)
                {
                    return source[i..(j + 1)];
                }
            }
            else if (c == ';' && open == '(' && depth == 0)
            {
                return source[i..j];
            }
        }

        return null;
    }

    private static string RepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Sangam.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Sangam.sln not found above " + AppContext.BaseDirectory);
    }

    [GeneratedRegex(@"\bpublic\s+(?:static\s+)?(?:async\s+)?[\w<>\[\]?,.() ]+?\s(?<name>\w+)\s*\((?<parameters>[^)]*)\)", RegexOptions.Singleline)]
    private static partial Regex Declaration();

    [GeneratedRegex(@"\b(?:public|private|internal|protected)\s+(?:static\s+)?(?:async\s+)?[\w<>\[\]?,.() ]+?\s(?<name>\w+)\s*\((?<parameters>[^)]*)\)", RegexOptions.Singleline)]
    private static partial Regex AnyDeclaration();

    [GeneratedRegex(@"(?<name>\w+)\(\s*operatorUserId\b")]
    private static partial Regex Call();
}
