using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Sangam.Identity.Application.Abstractions;
using Sangam.Identity.Domain.Entities;
using Sangam.Identity.Infrastructure.Persistence;
using Sangam.Identity.Infrastructure.Security;
using Sangam.Identity.Infrastructure.Seeding;
using Sangam.Identity.Infrastructure.Services;

namespace Sangam.Identity.Infrastructure;

/// <summary>Registers persistence, Identity, OpenIddict core and the infrastructure services.</summary>
public static class DependencyInjection
{
    /// <summary>Connection string name in <c>ConnectionStrings</c>.</summary>
    public const string ConnectionStringName = "Sangam";

    /// <summary>
    /// Adds <see cref="SangamDbContext"/> (pooled and via factory), ASP.NET Core Identity core
    /// with Argon2id hashing, OpenIddict core backed by EF, and the clock / email / audit services.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">Application configuration; reads <c>ConnectionStrings:Sangam</c> and <c>Sangam:PasswordHashing</c>.</param>
    /// <returns>The same collection, for chaining.</returns>
    public static IServiceCollection AddSangamInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        string connectionString = configuration.GetConnectionString(ConnectionStringName)
            ?? throw new InvalidOperationException($"Connection string '{ConnectionStringName}' is not configured.");

        services.AddDbContextFactory<SangamDbContext>(o => SangamDbContextOptions.Configure(o, connectionString));

        Argon2idOptions hashing = new();
        configuration.GetSection(Argon2idOptions.SectionName).Bind(hashing);
        services.AddSingleton(hashing);
        services.AddSingleton<IPasswordHasher<SangamUser>>(sp => new Argon2idPasswordHasher<SangamUser>(sp.GetRequiredService<Argon2idOptions>()));

        services.AddIdentityCore<SangamUser>(o =>
            {
                o.User.RequireUniqueEmail = true;
                o.SignIn.RequireConfirmedEmail = true;
                o.Password.RequiredLength = 12;
                o.Password.RequireNonAlphanumeric = false;
                o.Password.RequireUppercase = false;
                o.Password.RequireLowercase = false;
                o.Password.RequireDigit = false;
                o.Lockout.MaxFailedAccessAttempts = 5;
                o.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
                o.Lockout.AllowedForNewUsers = true;
            })
            .AddEntityFrameworkStores<SangamDbContext>()
            .AddDefaultTokenProviders();

        services.AddOpenIddict()
            .AddCore(o => o.UseEntityFrameworkCore().UseDbContext<SangamDbContext>());

        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<IEmailSender, LoggingEmailSender>();
        services.AddSingleton<IAuditWriter, EfAuditWriter>();
        services.AddScoped<DevelopmentSeeder>();

        return services;
    }
}
