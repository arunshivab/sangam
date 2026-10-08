using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Sangam.Identity.Infrastructure.Persistence;

/// <summary>The one place that knows how a <see cref="SangamDbContext"/> is configured.</summary>
public static class SangamDbContextOptions
{
    /// <summary>
    /// The Identity schema version every context uses (D-G: version 3 adds Identity's passkey store). Identity reads
    /// it from the application's services; a context made without them — the design-time factory, the migration
    /// bundle, test fixtures — gets these, so every context builds the same model.
    /// </summary>
    private static readonly Lazy<IServiceProvider> IdentityModelServices = new(() =>
    {
        ServiceCollection services = new();
        services.AddOptions();
        services.Configure<IdentityOptions>(o => o.Stores.SchemaVersion = IdentitySchemaVersions.Version3);
        return services.BuildServiceProvider();
    });

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
        if (builder.Options.FindExtension<CoreOptionsExtension>()?.ApplicationServiceProvider is null)
        {
            builder.UseApplicationServiceProvider(IdentityModelServices.Value);
        }
    }
}
