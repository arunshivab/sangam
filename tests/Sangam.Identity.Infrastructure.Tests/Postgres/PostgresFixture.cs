using Microsoft.EntityFrameworkCore;
using Sangam.Identity.Infrastructure.Persistence;

namespace Sangam.Identity.Infrastructure.Tests.Postgres;

/// <summary>
/// One migrated database per test run, shared by every class in the "postgres" collection
/// (which serialises them). <see cref="ResetAsync"/> truncates the mutable tables between tests.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private static readonly string[] MutableTables =
    [
        "audit_events",
        "org_memberships",
        "app_grants",
        "consents",
        "app_admins",
        "platform_operators",
        "roles",
        "organisations",
        "apps",
        "user_tokens",
        "user_logins",
        "user_claims",
        "users",
        "openiddict_tokens",
        "openiddict_authorizations",
        "openiddict_applications",
        "openiddict_scopes",
    ];

    public string ConnectionString { get; } = PostgresFactAttribute.ConnectionString ?? string.Empty;

    public bool IsAvailable => !string.IsNullOrWhiteSpace(ConnectionString);

    public SangamDbContext CreateContext()
    {
        DbContextOptionsBuilder<SangamDbContext> builder = new();
        SangamDbContextOptions.Configure(builder, ConnectionString);
        return new SangamDbContext(builder.Options);
    }

    public async Task InitializeAsync()
    {
        if (!IsAvailable)
        {
            return;
        }

        await using SangamDbContext db = CreateContext();
        await db.Database.MigrateAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>Empties every non-reference table. The append-only rules make TRUNCATE the only way to clear audit_events.</summary>
    public async Task ResetAsync()
    {
        await using SangamDbContext db = CreateContext();
        string sql = "TRUNCATE TABLE " + string.Join(", ", MutableTables) + " RESTART IDENTITY CASCADE;";
        await db.Database.ExecuteSqlRawAsync(sql);
    }
}

[CollectionDefinition("postgres")]
public sealed class PostgresTestGroup : ICollectionFixture<PostgresFixture>
{
}
