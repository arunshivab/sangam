namespace Sangam.Admin.Web.Tests;

/// <summary>A fact that needs a real PostgreSQL; skipped when <c>SANGAM_TEST_CONNECTION</c> is unset.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute()
    {
        if (!ConsoleFactory.HasDatabase)
        {
            Skip = $"{ConsoleFactory.ConnectionEnvironmentVariable} is not set; PostgreSQL-backed test skipped.";
        }
    }
}
