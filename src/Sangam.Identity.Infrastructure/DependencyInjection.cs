using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Sangam.Identity.Application.Abstractions;
using Sangam.Identity.Application.Accounts;
using Sangam.Identity.Application.Admin;
using Sangam.Identity.Application.Apps;
using Sangam.Identity.Application.Consents;
using Sangam.Identity.Application.Partners;
using Sangam.Identity.Application.Portal;
using Sangam.Identity.Application.Tenancy;
using Sangam.Identity.Domain.Entities;
using Sangam.Identity.Infrastructure.Accounts;
using Sangam.Identity.Infrastructure.Admin;
using Sangam.Identity.Infrastructure.Apps;
using Sangam.Identity.Infrastructure.Consents;
using Sangam.Identity.Infrastructure.Maintenance;
using Sangam.Identity.Infrastructure.Partners;
using Sangam.Identity.Infrastructure.Persistence;
using Sangam.Identity.Infrastructure.Portal;
using Sangam.Identity.Infrastructure.Security;
using Sangam.Identity.Infrastructure.Seeding;
using Sangam.Identity.Infrastructure.Services;
using Sangam.Identity.Infrastructure.Tenancy;

namespace Sangam.Identity.Infrastructure;

/// <summary>Registers persistence, Identity, OpenIddict core and the infrastructure services.</summary>
public static class DependencyInjection
{
    /// <summary>Configuration key: persist the data-protection key ring in the database (default true).</summary>
    public const string PersistKeysKey = "Sangam:DataProtection:PersistKeys";

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
        services.AddScoped<IPortalService, EfPortalService>();
        services.AddScoped<IEmailChangeService, EfEmailChangeService>();
        services.AddScoped<IAdminService, EfAdminService>();
        services.AddScoped<IPartnerService, EfPartnerService>();
        services.AddScoped<IInvitationService, EfInvitationService>();
        services.AddScoped<IMfaService, TotpMfaService>();
        services.AddSingleton<ISessionService, EfSessionService>();

        services.AddSingleton<IClock, SystemClock>();

        // Persist the data-protection key ring in the database so cookies and form tokens survive a
        // container replacement and every instance shares one ring (OI-037). Hosts started without a
        // database (some tests) switch this off.
        if (configuration.GetValue(PersistKeysKey, true))
        {
            services.AddDataProtection().SetApplicationName("Sangam").PersistKeysToDbContext<SangamDbContext>();
        }

        if (configuration.GetValue<bool>("Sangam:Email:UseOutbox"))
        {
            // Development/Testing: capture messages for /dev/outbox and the tests.
            services.AddSingleton<InMemoryEmailOutbox>();
            services.AddSingleton<IEmailSender>(sp => sp.GetRequiredService<InMemoryEmailOutbox>());
        }
        else if (!string.IsNullOrWhiteSpace(configuration[EmailSenderGuard.SmtpHostKey]))
        {
            // PR-09: e-mail through Anjal by SMTP submission; settings are the founder’s (OI-027).
            services.Configure<SmtpOptions>(configuration.GetSection(SmtpOptions.SectionName));
            services.AddSingleton<IEmailSender, SmtpEmailSender>();
        }
        else
        {
            // No sender configured: refuse to send, and never log content (OI-038).
            services.AddSingleton<IEmailSender, UnavailableEmailSender>();
        }

        // The chained writer is a singleton; the scoped decorator adds the current client (OI-039).
        services.AddSingleton<EfAuditWriter>();
        services.AddScoped<ClientContext>();
        services.AddScoped<IClientContext>(sp => sp.GetRequiredService<ClientContext>());
        services.AddScoped<IAuditWriter>(sp => new ClientAwareAuditWriter(sp.GetRequiredService<EfAuditWriter>(), sp.GetRequiredService<IClientContext>()));
        services.AddScoped<DevelopmentSeeder>();
        services.AddHostedService<AccountPurgeService>();

        return services;
    }
}
