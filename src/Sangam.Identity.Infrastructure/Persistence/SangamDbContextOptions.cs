using Microsoft.EntityFrameworkCore;

namespace Sangam.Identity.Infrastructure.Persistence;

/// <summary>The one place that knows how a <see cref="SangamDbContext"/> is configured.</summary>
public static class SangamDbContextOptions
{
    /// <summary>Applies the Npgsql provider, snake_case naming and OpenIddict to <paramref name="builder"/>.</summary>
    /// <param name="builder">Options builder.</param>
    /// <param name="connectionString">PostgreSQL connection string.</param>
    public static void Configure(DbContextOptionsBuilder builder, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        builder
            .UseNpgsql(connectionString, npgsql => npgsql.MigrationsAssembly(AssemblyReference.Assembly.GetName().Name))
            .UseSnakeCaseNamingConvention()
            .UseOpenIddict();
    }
}
