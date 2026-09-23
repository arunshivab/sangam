using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Sangam.Identity.Infrastructure.Persistence;

/// <summary>
/// Design-time factory for <c>dotnet ef migrations add</c>. Uses <c>SANGAM_CONNECTION</c> from
/// the environment when set, otherwise the local development connection string. Adding a
/// migration never opens the connection; only <c>database update</c> does.
/// </summary>
public sealed class SangamDbContextFactory : IDesignTimeDbContextFactory<SangamDbContext>
{
    /// <summary>The local development connection string (matches deploy/postgres/init.sql).</summary>
    public const string DevelopmentConnectionString =
        "Host=localhost;Port=5432;Database=sangam_identity;Username=sangam_identity;Password=sangam_dev";

    /// <inheritdoc />
    public SangamDbContext CreateDbContext(string[] args)
    {
        string connectionString = Environment.GetEnvironmentVariable("SANGAM_CONNECTION") ?? DevelopmentConnectionString;
        DbContextOptionsBuilder<SangamDbContext> builder = new();
        SangamDbContextOptions.Configure(builder, connectionString);
        return new SangamDbContext(builder.Options);
    }
}
