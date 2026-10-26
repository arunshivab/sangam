using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Sangam.Identity.Infrastructure.Persistence;
using Sangam.Identity.Infrastructure.Tests.Postgres;

namespace Sangam.Identity.Infrastructure.Tests.Persistence;

/// <summary>
/// rc.16 (ASVS V1.2.2): each host signs in to the database as its own role. Every host reads and writes the application's
/// tables; only the identity server deletes archived audit events and touches its own sign-in tables; no host writes the
/// migration history or makes tables.
/// </summary>
[Collection("postgres")]
public sealed partial class HostRolesTests
{
    private readonly PostgresFixture _pg;

    public HostRolesTests(PostgresFixture pg)
    {
        _pg = pg;
    }

    [Fact]
    public void TheDeploymentScript_IsExactlyWhatTheMigrationApplies()
    {
        string script = File.ReadAllText(Path.Combine(RepoRoot(), "deploy", "production", "postgres", "grants.sql")).ReplaceLineEndings("\n");
        string body = script[script.IndexOf("DO $grants$", StringComparison.Ordinal)..].TrimEnd();
        Assert.Equal(HostRoles.GrantsSql.ReplaceLineEndings("\n").TrimEnd(), body);
    }

    [Fact]
    public void TheRolesScript_MakesEveryRoleTheGrantsName()
    {
        string roles = File.ReadAllText(Path.Combine(RepoRoot(), "deploy", "production", "postgres", "roles.sql"));
        foreach (string role in new[] { HostRoles.Owner, HostRoles.Group, HostRoles.Identity, HostRoles.Portal, HostRoles.Admin, HostRoles.Partner })
        {
            Assert.Contains($"rolname = '{role}'", roles, StringComparison.Ordinal);
        }
    }

    [PostgresFact]
    public async Task EachHost_HasOnlyTheRightsItNeeds()
    {
        string roles = File.ReadAllText(Path.Combine(RepoRoot(), "deploy", "production", "postgres", "roles.sql"));
        string makeRoles = RolesBlock().Match(roles).Value;
        Assert.False(string.IsNullOrEmpty(makeRoles));

        await using (SangamDbContext db = _pg.CreateContext())
        {
            // Roles belong to the whole database server: making them needs a superuser (CI's test user is one). A local
            // database whose test user may not make roles skips this check; CI never does.
            bool mayMakeRoles = await db.Database.SqlQueryRaw<bool>("SELECT rolsuper OR rolcreaterole AS \"Value\" FROM pg_roles WHERE rolname = current_user").SingleAsync();
            if (!mayMakeRoles)
            {
                Assert.NotEqual("true", Environment.GetEnvironmentVariable("CI"));
                return;
            }

            await db.Database.ExecuteSqlRawAsync(makeRoles);
            await db.Database.ExecuteSqlRawAsync(HostRoles.GrantsSql);
        }

        // Every host reads and writes ordinary tables and adds to the audit log.
        foreach (string host in new[] { HostRoles.Identity, HostRoles.Portal, HostRoles.Admin, HostRoles.Partner })
        {
            Assert.Null(await TryAsync(host, "SELECT count(*) FROM users"));
            Assert.Null(await TryAsync(host, "UPDATE users SET updated_at = updated_at WHERE false"));
            Assert.Null(await TryAsync(host, "INSERT INTO audit_events (action, actor_type, occurred_at) SELECT 'x', 'system', now() WHERE false"));
            Assert.Equal("42501", await TryAsync(host, "UPDATE audit_events SET action = action WHERE false"));
            Assert.Equal("42501", await TryAsync(host, "DELETE FROM \"__EFMigrationsHistory\" WHERE false"));
            Assert.Equal("42501", await TryAsync(host, "CREATE TABLE host_made_this (i int)"));
        }

        // The identity server alone deletes archived audit events and uses its own sign-in tables.
        Assert.Null(await TryAsync(HostRoles.Identity, "DELETE FROM audit_events WHERE false"));
        foreach (string table in HostRoles.IdentityOnlyTables)
        {
            Assert.Null(await TryAsync(HostRoles.Identity, $"SELECT count(*) FROM {table}"));
        }

        foreach (string host in new[] { HostRoles.Portal, HostRoles.Admin, HostRoles.Partner })
        {
            Assert.Equal("42501", await TryAsync(host, "DELETE FROM audit_events WHERE false"));
            foreach (string table in HostRoles.IdentityOnlyTables)
            {
                Assert.Equal("42501", await TryAsync(host, $"SELECT count(*) FROM {table}"));
            }
        }
    }

    /// <summary>Runs one statement as <paramref name="role"/>, inside a transaction that is rolled back; the SQL state of a failure, or null.</summary>
    private async Task<string?> TryAsync(string role, string sql)
    {
        NpgsqlConnection connection = new(_pg.ConnectionString);
        await using (connection)
        {
            await connection.OpenAsync();
            NpgsqlTransaction tx = await connection.BeginTransactionAsync();
            await using (tx)
            {
                await using (NpgsqlCommand become = new($"SET LOCAL ROLE {role}", connection, tx))
                {
                    await become.ExecuteNonQueryAsync();
                }

                try
                {
                    await using NpgsqlCommand command = new(sql, connection, tx);
                    await command.ExecuteNonQueryAsync();
                    return null;
                }
                catch (PostgresException e)
                {
                    return e.SqlState;
                }
                finally
                {
                    await tx.RollbackAsync();
                }
            }
        }
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

    [GeneratedRegex(@"DO \$roles\$.*?\$roles\$;", RegexOptions.Singleline)]
    private static partial Regex RolesBlock();
}
