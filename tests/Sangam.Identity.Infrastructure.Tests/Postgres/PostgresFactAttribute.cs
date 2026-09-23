namespace Sangam.Identity.Infrastructure.Tests.Postgres;

/// <summary>
/// A fact that needs a real PostgreSQL. Runs when <c>SANGAM_TEST_CONNECTION</c> is set
/// (locally: the sangam_identity_test database from deploy/postgres/init.sql; in CI: the
/// Ubuntu job's service container) and is skipped otherwise, so Windows runners and machines
/// without PostgreSQL still pass.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class PostgresFactAttribute : FactAttribute
{
    public const string EnvironmentVariable = "SANGAM_TEST_CONNECTION";

    public PostgresFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(ConnectionString))
        {
            Skip = $"{EnvironmentVariable} is not set; PostgreSQL-backed test skipped.";
        }
    }

    public static string? ConnectionString => Environment.GetEnvironmentVariable(EnvironmentVariable);
}
