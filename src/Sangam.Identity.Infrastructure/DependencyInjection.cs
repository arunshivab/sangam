using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sangam.Identity.Application.Abstractions;
using Sangam.Identity.Application.Accounts;
using Sangam.Identity.Application.Admin;
using Sangam.Identity.Application.Apps;
using Sangam.Identity.Application.Consents;
using Sangam.Identity.Application.Partners;
using Sangam.Identity.Application.Passkeys;
using Sangam.Identity.Application.Portal;
using Sangam.Identity.Application.Security;
using Sangam.Identity.Application.Signatures;
using Sangam.Identity.Application.Sms;
using Sangam.Identity.Application.Tenancy;
using Sangam.Identity.Domain.Entities;
using Sangam.Identity.Infrastructure.Accounts;
using Sangam.Identity.Infrastructure.Admin;
using Sangam.Identity.Infrastructure.Apps;
using Sangam.Identity.Infrastructure.Audit;
using Sangam.Identity.Infrastructure.Consents;
using Sangam.Identity.Infrastructure.Grievances;
using Sangam.Identity.Infrastructure.Maintenance;
using Sangam.Identity.Infrastructure.Messaging;
using Sangam.Identity.Infrastructure.Monitoring;
using Sangam.Identity.Infrastructure.Partners;
using Sangam.Identity.Infrastructure.Passkeys;
using Sangam.Identity.Infrastructure.Persistence;
using Sangam.Identity.Infrastructure.Policies;
using Sangam.Identity.Infrastructure.Portal;
using Sangam.Identity.Infrastructure.Security;
using Sangam.Identity.Infrastructure.Seeding;
using Sangam.Identity.Infrastructure.Services;
using Sangam.Identity.Infrastructure.Signatures;
using Sangam.Identity.Infrastructure.Sms;
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
    /// <param name="configuration">Application configuration; reads <c>ConnectionStrings:Sangam</c>, <c>Sangam:PasswordHashing</c>, <c>Sangam:Otp</c>, <c>Sangam:Email:UseOutbox</c> and <c>Sangam:Anjal</c>.</param>
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
                // rc.5 (ASVS V2.1.1, V2.1.9): length, not character types. PasswordStrength enforces it first with the
                // built-in list of common passwords; PolicyPasswordValidator adds what organisations require.
                o.Password.RequiredLength = Sangam.Identity.Application.Security.PasswordStrength.MinimumLength;
                o.Password.RequireNonAlphanumeric = false;
                o.Password.RequireUppercase = false;
                o.Password.RequireLowercase = false;
                o.Password.RequireDigit = false;
                o.Password.RequiredUniqueChars = 1;
                // UnknownAddressLockout mirrors these two for addresses with no account (D-L).
                o.Lockout.MaxFailedAccessAttempts = 5;
                o.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
                o.Lockout.AllowedForNewUsers = true;
                o.Stores.SchemaVersion = IdentitySchemaVersions.Version3;
            })
            .AddEntityFrameworkStores<SangamDbContext>()
            .AddDefaultTokenProviders()
            .AddPasswordValidator<PolicyPasswordValidator>()
            // R7: authenticator secrets encrypted and recovery codes hashed (ASVS V2.8.2, V2.6.2); authenticator codes
            // accepted once (V2.8.4).
            .AddUserStore<Accounts.SangamUserStore>()
            .AddTokenProvider<Accounts.SangamAuthenticatorTokenProvider>(TokenOptions.DefaultAuthenticatorProvider);

        // PR-16: platform → application → organisation security policies, and the breached-password check.
        PolicySettings policies = PolicySettings.From(configuration);
        services.AddSingleton(policies);
        services.AddScoped<ISecurityPolicyService, EfSecurityPolicyService>();
        // rc.5 (D-J revised): the built-in list first, then Pwned Passwords by k-anonymity; an outage falls back to the list.
        services.AddHttpClient(PwnedPasswordsChecker.HttpClientName, c => c.DefaultRequestHeaders.UserAgent.ParseAdd("SangamID-breach-check"));
        services.AddSingleton<PwnedPasswordsChecker>();
        services.AddSingleton<IBreachedPasswordChecker>(sp => sp.GetRequiredService<PwnedPasswordsChecker>());
        services.AddSingleton<IBreachListStatus>(sp => sp.GetRequiredService<PwnedPasswordsChecker>());

        services.AddOpenIddict()
            .AddCore(o => o.UseEntityFrameworkCore().UseDbContext<SangamDbContext>());

        OtpOptions otp = new();
        configuration.GetSection(OtpOptions.SectionName).Bind(otp);
        services.AddSingleton(otp);
        RegistrationOptions registration = new();
        configuration.GetSection(RegistrationOptions.SectionName).Bind(registration);
        services.AddSingleton(registration);
        services.AddScoped<OneTimeCodeService>();
        services.AddScoped<IAccountService, AccountService>();
        services.AddScoped<Accounts.SecurityNotices>();
        services.AddSingleton(sp => new UnknownAddressLockout(sp.GetRequiredService<IDbContextFactory<SangamDbContext>>(), sp.GetRequiredService<IClock>(), 5, TimeSpan.FromMinutes(15)));
        services.AddScoped<IAppDirectory, EfAppDirectory>();
        services.AddScoped<IConsentService, EfConsentService>();
        services.AddScoped<ITenancyQuery, EfTenancyQuery>();
        services.AddScoped<IManagementService, EfManagementService>();
        services.AddScoped<IPortalService, EfPortalService>();
        services.AddScoped<IEmailChangeService, EfEmailChangeService>();
        // D-G: passkeys on ASP.NET Core Identity's built-in WebAuthn support (Fido2NetLib removed).
        services.Configure<IdentityPasskeyOptions>(o => PasskeySettings.Apply(o, configuration));
        services.AddHttpContextAccessor();
        services.AddScoped<IPasskeyHandler<SangamUser>, PasskeyHandler<SangamUser>>();
        services.AddScoped<IPasskeyService, EfPasskeyService>();
        AddSms(services, configuration);
        // PR-17: the token issuer is the identity server's (it holds the signing keys); hosts that never sign do not resolve it.
        services.AddScoped<ISignatureService, EfSignatureService>();
        // PR-19: branding and message templates by level.
        services.AddScoped<Application.Customisation.CurrentApplication>();
        // rc.5 (ASVS V12.4.2): every uploaded logo goes through the server's ClamAV (shared with Anjal).
        services.AddSingleton<Customisation.IFileScanner>(sp => new Customisation.ClamAvScanner(configuration, sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<Customisation.ClamAvScanner>>()));
        services.AddScoped<Customisation.EfCustomisationService>();
        services.AddScoped<Application.Customisation.ICustomisationService>(sp => sp.GetRequiredService<Customisation.EfCustomisationService>());
        services.AddScoped<Application.Customisation.IMessageTemplates>(sp => sp.GetRequiredService<Customisation.EfCustomisationService>());
        services.AddSingleton<ISignatureTokenIssuer, UnavailableSignatureTokenIssuer>();
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
            IDataProtectionBuilder keyRing = services.AddDataProtection().SetApplicationName("Sangam").PersistKeysToDbContext<SangamDbContext>();
            KeyRingProtection.Apply(keyRing, configuration);
        }

        // D-B / D-M: Anjal is Sangam's single messaging gateway, reached by its API with an API key.
        AnjalOptions anjal = AnjalOptions.From(configuration);
        services.AddSingleton(anjal);
        if (anjal.Configured)
        {
            services.AddSingleton(sp => new AnjalClient(
                new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
                {
                    BaseAddress = new Uri(anjal.BaseUrl!.EndsWith('/') ? anjal.BaseUrl : anjal.BaseUrl + "/"),
                    Timeout = TimeSpan.FromSeconds(Math.Max(1, anjal.TimeoutSeconds)),
                },
                anjal,
                sp.GetRequiredService<ILogger<AnjalClient>>()));
        }

        if (configuration.GetValue<bool>("Sangam:Email:UseOutbox") && configuration.GetValue<bool>(DevOutboxStore.SharedKey))
        {
            // V-11: in Development every host's outbox also writes to one table, shown on the identity server's /dev/outbox.
            services.AddSingleton<DevOutboxStore>();
        }

        if (configuration.GetValue<bool>("Sangam:Email:UseOutbox"))
        {
            // Development/Testing: capture messages for /dev/outbox and the tests.
            services.AddSingleton<InMemoryEmailOutbox>();
            services.AddSingleton<IEmailSender>(sp => sp.GetRequiredService<InMemoryEmailOutbox>());
        }
        else if (anjal.Configured)
        {
            services.AddSingleton<AnjalEmailSender>();
            if (configuration.GetValue(EmailSenderGuard.DeliverInBackgroundKey, true))
            {
                // A page never waits for Anjal; failures are counted for the monitoring page (D-H).
                services.AddSingleton(sp => new BackgroundEmailSender(sp.GetRequiredService<AnjalEmailSender>(), sp.GetRequiredService<ILogger<BackgroundEmailSender>>()));
                services.AddSingleton<IEmailSender>(sp => sp.GetRequiredService<BackgroundEmailSender>());
                services.AddHostedService(sp => sp.GetRequiredService<BackgroundEmailSender>());
            }
            else
            {
                services.AddSingleton<IEmailSender>(sp => sp.GetRequiredService<AnjalEmailSender>());
            }
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
        services.AddScoped<ClientRegistration>();
        // PR-22: the SAML identity provider's state and the console's service providers.
        services.AddScoped<Saml.SamlIdentityProvider>();
        services.AddScoped<Application.Saml.ISamlAdminService>(sp => sp.GetRequiredService<Saml.SamlIdentityProvider>());

        // D-D: the grievance log in the operator console.
        services.AddSingleton<GrievanceClock>();
        services.AddScoped<Application.Grievances.IGrievanceService, EfGrievanceService>();

        // PR-23/24: SCIM provisioning and webhooks call addresses partners typed in: https only, no redirects, and never
        // a private network address unless Sangam:Outbound:AllowPrivateNetworks (Development and Testing) says so.
        services.AddSingleton(sp => new Provisioning.OutboundSettings(Provisioning.OutboundHttp.AllowPrivate(configuration, sp.GetService<Microsoft.Extensions.Hosting.IHostEnvironment>()?.EnvironmentName ?? "Production")));
        services.AddHttpClient(Provisioning.OutboundHttp.ClientName, c => c.Timeout = Provisioning.OutboundHttp.Timeout)
            .ConfigurePrimaryHttpMessageHandler(sp => Provisioning.OutboundHttp.CreateHandler(sp.GetRequiredService<Provisioning.OutboundSettings>().AllowPrivate));
        services.AddScoped<Tenancy.MembershipExpiry>();
        services.AddScoped<Application.Attributes.IAttributeService, Attributes.EfAttributeService>();
        services.AddScoped<Application.Evidence.IEvidencePackService, Evidence.EfEvidencePackService>();

        // PR-26: identity verification through DigiLocker (off unless Sangam:DigiLocker:Enabled).
        services.AddSingleton(Verification.DigiLockerSettings.From(configuration));
        services.AddHttpClient(Verification.DigiLockerSettings.ClientName, c => c.Timeout = Provisioning.OutboundHttp.Timeout)
            .ConfigurePrimaryHttpMessageHandler(sp => Provisioning.OutboundHttp.CreateHandler(sp.GetRequiredService<Provisioning.OutboundSettings>().AllowPrivate));
        services.AddScoped<Verification.DigiLockerClient>();
        services.AddScoped<Verification.EfIdentityVerificationService>();
        services.AddScoped<Application.Verification.IIdentityVerificationService>(sp => sp.GetRequiredService<Verification.EfIdentityVerificationService>());
        services.AddScoped<Provisioning.AppEventDispatcher>();
        services.AddScoped<Provisioning.IntegrationAlerts>();
        services.AddScoped<Provisioning.ScimProvisioner>();
        services.AddScoped<Provisioning.WebhookSender>();
        services.AddScoped<Application.Provisioning.IWebhookService, Provisioning.EfWebhookService>();
        services.AddScoped<Application.Provisioning.IProvisioningService, Provisioning.EfProvisioningService>();

        // D-A: a year live, then the encrypted, anonymised archive; purged at seven years.
        services.AddSingleton(AuditArchiveOptions.From(configuration));
        services.AddSingleton<AuditArchiver>();
        services.AddScoped<InactivityService>();
        services.AddHostedService<AccountPurgeService>();

        // PR-32: the audit log streamed to a SIEM (off unless Sangam:Siem:Enabled; one host at a time).
        services.AddSingleton(Siem.SiemOptions.From(configuration));
        services.AddSingleton<Siem.SiemForwarder>();
        services.AddHostedService<Siem.SiemStreamingService>();

        // D-K: support resets of two-step sign-in wait out a cooling-off period; D-H/D-K: alerts to the founder.
        services.AddScoped<EfMfaResetService>();
        services.AddScoped<IMfaResetService>(sp => sp.GetRequiredService<EfMfaResetService>());
        services.AddScoped<Application.Verification.IAccountRecoveryService, EfAccountRecoveryService>();
        services.AddScoped<IPlatformAlerts, PlatformAlerts>();
        services.AddHostedService<MfaResetApplier>();

        // D-H: Sangam's own monitoring — metrics into its database, a page in the operator console, alerts through Anjal.
        MonitoringOptions monitoring = MonitoringOptions.From(configuration);
        services.AddSingleton(monitoring);
        services.AddSingleton<HostProbe>();
        services.AddSingleton<MetricsRecorder>();
        services.AddHostedService(sp => sp.GetRequiredService<MetricsRecorder>());
        services.AddSingleton<TlsProbe>();
        services.AddScoped<EfMonitoringService>();
        services.AddScoped<Application.Monitoring.IMonitoringService>(sp => sp.GetRequiredService<EfMonitoringService>());
        services.AddSingleton<AlertEvaluator>();
        services.AddHostedService(sp => sp.GetRequiredService<AlertEvaluator>());

        return services;
    }

    /// <summary>
    /// SMS (PR-15): the settings, the code service and the provider chain. Providers without an adapter in
    /// this build resolve to a sender that refuses every message; the start-up guard stops production first.
    /// </summary>
    private static void AddSms(IServiceCollection services, IConfiguration configuration)
    {
        SmsSettings sms = SmsSettings.From(configuration);
        services.AddSingleton(sms);
        services.AddScoped<ISmsCodeService, EfSmsCodeService>();
        services.AddScoped<SmsNoticeSender>();

        if (string.Equals(sms.Provider, SmsSettings.OutboxProvider, StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton<InMemorySmsOutbox>();
            services.AddSingleton<ISmsSender>(sp => sp.GetRequiredService<InMemorySmsOutbox>());
        }
        else if (string.Equals(sms.Provider, AnjalSmsSender.ProviderName, StringComparison.OrdinalIgnoreCase) && AnjalOptions.From(configuration).Configured)
        {
            // D-M: SMS through Anjal, which fails over between two aggregators itself.
            services.AddSingleton<ISmsSender, AnjalSmsSender>();
        }
        else
        {
            services.AddSingleton<ISmsSender, UnavailableSmsSender>();
        }
    }
}
