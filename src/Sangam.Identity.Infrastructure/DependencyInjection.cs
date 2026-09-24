using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Sangam.Identity.Application.Abstractions;
using Sangam.Identity.Application.Accounts;
using Sangam.Identity.Application.Apps;
using Sangam.Identity.Application.Consents;
using Sangam.Identity.Application.Tenancy;
using Sangam.Identity.Domain.Entities;
using Sangam.Identity.Infrastructure.Accounts;
using Sangam.Identity.Infrastructure.Apps;
using Sangam.Identity.Infrastructure.Consents;
using Sangam.Identity.Infrastructure.Persistence;
using Sangam.Identity.Infrastructure.Security;
using Sangam.Identity.Infrastructure.Seeding;
using Sangam.Identity.Infrastructure.Services;
using Sangam.Identity.Infrastructure.Tenancy;

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
    /// <param name="configuration">Application configuration; reads <c>ConnectionStrings:Sangam</c>, <c>Sangam:PasswordHashing</c>, <c>Sangam:Otp</c> and <c>Sangam:Email:UseOutbox</c>.</param>
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
                // Same policy as Anjal; PasswordStrength enforces it first with the blocklist.
                o.Password.RequiredLength = Sangam.Identity.Application.Security.PasswordStrength.MinimumLength;
                o.Password.RequireNonAlphanumeric = true;
                o.Password.RequireUppercase = true;
                o.Password.RequireLowercase = true;
                o.Password.RequireDigit = true;
                o.Lockout.MaxFailedAccessAttempts = 5;
                o.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
                o.Lockout.AllowedForNewUsers = true;
            })
            .AddEntityFrameworkStores<SangamDbContext>()
            .AddDefaultTokenProviders();

        services.AddOpenIddict()
            .AddCore(o => o.UseEntityFrameworkCore().UseDbContext<SangamDbContext>());

        OtpOptions otp = new();
        configuration.GetSection(OtpOptions.SectionName).Bind(otp);
        services.AddSingleton(otp);
        services.AddScoped<OneTimeCodeService>();
        services.AddScoped<IAccountService, AccountService>();
        services.AddScoped<IAppDirectory, EfAppDirectory>();
        services.AddScoped<IConsentService, EfConsentService>();
        services.AddScoped<ITenancyQuery, EfTenancyQuery>();
        services.AddScoped<IManagementService, EfManagementService>();

        services.AddSingleton<IClock, SystemClock>();
        if (configuration.GetValue<bool>("Sangam:Email:UseOutbox"))
        {
            // Development/Testing: capture messages for /dev/outbox and the tests.
            services.AddSingleton<InMemoryEmailOutbox>();
            services.AddSingleton<IEmailSender>(sp => sp.GetRequiredService<InMemoryEmailOutbox>());
        }
        else
        {
            services.AddSingleton<IEmailSender, LoggingEmailSender>();
        }

        services.AddSingleton<IAuditWriter, EfAuditWriter>();
        services.AddScoped<DevelopmentSeeder>();

        return services;
    }
}
