using System.Diagnostics;
using System.Globalization;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Sangam.Identity.Infrastructure.Persistence;

namespace Sangam.Identity.Infrastructure;

/// <summary>
/// What every Sangam host needs to run behind Caddy (OI-037): forwarded headers from trusted proxies
/// only, and health endpoints.
/// </summary>
public static class WebHosting
{
    /// <summary>Configuration key: comma-separated proxy addresses whose forwarded headers are trusted.</summary>
    public const string KnownProxiesKey = "Sangam:ForwardedHeaders:KnownProxies";

    /// <summary>Configuration key: comma-separated networks (CIDR) whose forwarded headers are trusted.</summary>
    public const string KnownNetworksKey = "Sangam:ForwardedHeaders:KnownNetworks";

    /// <summary>Environment variable naming the secrets directory (default <c>/run/secrets</c>).</summary>
    public const string SecretsDirectoryVariable = "SANGAM_SECRETS_DIR";

    /// <summary>Health endpoint: the process is up.</summary>
    public const string LivePath = "/health/live";

    /// <summary>Health endpoint: the process can serve (database reachable).</summary>
    public const string ReadyPath = "/health/ready";

    /// <summary>
    /// Registers forwarded-headers handling and health checks. Without configured proxies only
    /// loopback is trusted, so a client can never spoof its address by sending the header itself.
    /// </summary>
    /// <param name="services">Services.</param>
    /// <param name="configuration">Configuration.</param>
    public static IServiceCollection AddSangamWebHosting(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        services.Configure<ForwardedHeadersOptions>(o =>
        {
            o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            foreach (string proxy in Split(configuration[KnownProxiesKey]))
            {
                o.KnownProxies.Add(IPAddress.Parse(proxy));
            }

            foreach (string network in Split(configuration[KnownNetworksKey]))
            {
                o.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(network));
            }
        });
        services.AddHealthChecks().AddCheck<DatabaseHealthCheck>("database", tags: ["ready"]);

        // D-H: no third party and no OpenTelemetry. Each host records its own metrics into Sangam's database
        // (Monitoring/MetricsRecorder); the operator console's monitoring page reads them.
        return services;
    }

    /// <summary>
    /// Counts every request by status class and times it (D-H), for the monitoring page's error rate, response times
    /// and rate-limit hits (429). The health endpoints are left out, so a watchdog's calls do not drown the numbers.
    /// Put it first, so it sees the final status of everything after it.
    /// </summary>
    /// <param name="app">The application.</param>
    public static IApplicationBuilder UseSangamRequestMetrics(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/health", StringComparison.OrdinalIgnoreCase))
            {
                await next(context).ConfigureAwait(false);
                return;
            }

            long started = Stopwatch.GetTimestamp();
            try
            {
                await next(context).ConfigureAwait(false);
            }
            finally
            {
                int status = context.Response.StatusCode;
                Monitoring.SangamMetrics.RequestCount.Add(1, new KeyValuePair<string, object?>("status", Monitoring.SangamMetrics.StatusClass(status)));
                Monitoring.SangamMetrics.RequestMilliseconds.Record(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }
        });
    }

    /// <summary>
    /// Whether the app should redirect plain HTTP to HTTPS itself (V-07). Not in Development or Testing, which
    /// serve plain HTTP on localhost; and not behind a trusted reverse proxy (Caddy), which terminates TLS,
    /// redirects HTTP itself and health-checks the app over plain HTTP — there the middleware could only warn
    /// "Failed to determine the https port for redirect".
    /// </summary>
    /// <param name="environmentName">The host environment name.</param>
    /// <param name="configuration">Configuration.</param>
    public static bool ShouldRedirectToHttps(string environmentName, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(environmentName);
        ArgumentNullException.ThrowIfNull(configuration);
        bool local = string.Equals(environmentName, "Development", StringComparison.OrdinalIgnoreCase)
            || string.Equals(environmentName, "Testing", StringComparison.OrdinalIgnoreCase);
        bool behindProxy = Split(configuration[KnownProxiesKey]).Length > 0 || Split(configuration[KnownNetworksKey]).Length > 0;
        return !local && !behindProxy;
    }

    /// <summary>Redirects HTTP to HTTPS only where <see cref="ShouldRedirectToHttps"/> says the app should (V-07).</summary>
    /// <param name="app">The application.</param>
    /// <param name="environmentName">The host environment name.</param>
    /// <param name="configuration">Configuration.</param>
    public static IApplicationBuilder UseSangamHttpsRedirection(this IApplicationBuilder app, string environmentName, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(app);
        return ShouldRedirectToHttps(environmentName, configuration) ? app.UseHttpsRedirection() : app;
    }

    /// <summary>
    /// Adds Docker secrets as configuration: each file in the secrets directory is one key, with
    /// <c>__</c> for <c>:</c> (for example <c>ConnectionStrings__Sangam</c>). Secrets never sit in a
    /// settings file or an image (PR-10).
    /// </summary>
    /// <param name="configuration">The configuration builder.</param>
    public static IConfigurationBuilder AddSangamSecretFiles(this IConfigurationBuilder configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        string directory = Environment.GetEnvironmentVariable(SecretsDirectoryVariable) ?? "/run/secrets";
        if (!Directory.Exists(directory))
        {
            return configuration;
        }

        // Certificates (.pfx) live alongside the secrets but are files, not settings.
        return configuration.AddKeyPerFile(source =>
        {
            source.FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(directory);
            source.Optional = true;
            source.IgnoreCondition = name => name.StartsWith("ignore.", StringComparison.Ordinal) || name.EndsWith(".pfx", StringComparison.OrdinalIgnoreCase);
        });
    }

    /// <summary>The culture used when the request names none Sangam supports: English (India), so dates read dd/mm/yyyy (V-05).</summary>
    public const string DefaultCulture = "en-IN";

    /// <summary>
    /// Cultures a request may select (by query string, culture cookie, the OpenID Connect <c>ui_locales</c> an
    /// application sends, or Accept-Language). Hindi and Malayalam have translated screens (PR-18); Tamil is accepted
    /// for its date and number formats and shows English text until its catalogue is written.
    /// </summary>
    public static readonly IReadOnlyList<string> SupportedCultures = ["en-IN", "hi-IN", "ml-IN", "ta-IN"];

    /// <summary>
    /// Sets each request's culture: one Sangam supports if the request asks for it, otherwise
    /// <see cref="DefaultCulture"/>. A browser sending only en-US therefore gets en-IN, not US dates.
    /// In order: <c>?culture=</c>, the language picker's cookie, the signed-in person's profile language
    /// (applied after authentication by <see cref="UseSangamProfileCulture"/>), an application's <c>ui_locales</c>,
    /// then Accept-Language.
    /// </summary>
    /// <param name="app">The application.</param>
    public static IApplicationBuilder UseSangamRequestCulture(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        CultureInfo[] cultures = [.. SupportedCultures.Select(c => new CultureInfo(c))];
        return app.UseRequestLocalization(new RequestLocalizationOptions
        {
            DefaultRequestCulture = new RequestCulture(DefaultCulture),
            SupportedCultures = cultures,
            SupportedUICultures = cultures,
            FallBackToParentCultures = false,
            FallBackToParentUICultures = false,
            RequestCultureProviders =
            [
                new QueryStringRequestCultureProvider(),
                new CookieRequestCultureProvider(),
                new UiLocalesRequestCultureProvider(),
                new AcceptLanguageHeaderRequestCultureProvider(),
            ],
        });
    }

    /// <summary>
    /// After authentication: when the language was not chosen explicitly (by <c>?culture=</c> or the language
    /// picker's cookie), uses the signed-in person's profile language, the <c>locale</c> claim (PR-18).
    /// </summary>
    /// <param name="app">The application.</param>
    public static IApplicationBuilder UseSangamProfileCulture(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.Use(async (context, next) =>
        {
            IRequestCultureFeature? chosen = context.Features.Get<IRequestCultureFeature>();
            bool explicitChoice = chosen?.Provider is QueryStringRequestCultureProvider or CookieRequestCultureProvider;
            string? locale = context.User.FindFirst("locale")?.Value;
            string? match = locale is null ? null : SupportedCultures.FirstOrDefault(c => string.Equals(c, locale, StringComparison.OrdinalIgnoreCase));
            if (!explicitChoice && match is not null)
            {
                CultureInfo culture = new(match);
                CultureInfo.CurrentCulture = culture;
                CultureInfo.CurrentUICulture = culture;
                context.Features.Set<IRequestCultureFeature>(new RequestCultureFeature(new RequestCulture(culture), provider: null));
            }

            await next(context).ConfigureAwait(false);
        });
    }

    /// <summary>Maps the anonymous health endpoints.</summary>
    /// <param name="endpoints">Endpoints.</param>
    public static IEndpointRouteBuilder MapSangamHealth(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        endpoints.MapHealthChecks(LivePath, new HealthCheckOptions { Predicate = _ => false }).AllowAnonymous();
        endpoints.MapHealthChecks(ReadyPath, new HealthCheckOptions { Predicate = c => c.Tags.Contains("ready") }).AllowAnonymous();
        return endpoints;
    }

    private static string[] Split(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? [] : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}

/// <summary>Ready when the database answers.</summary>
public sealed class DatabaseHealthCheck : IHealthCheck
{
    private readonly IDbContextFactory<SangamDbContext> _contexts;

    /// <summary>Initialises the check.</summary>
    /// <param name="contexts">Database context factory.</param>
    public DatabaseHealthCheck(IDbContextFactory<SangamDbContext> contexts)
    {
        _contexts = contexts ?? throw new ArgumentNullException(nameof(contexts));
    }

    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        SangamDbContext db = await _contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            return await db.Database.CanConnectAsync(cancellationToken).ConfigureAwait(false)
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy("The database is not reachable.");
        }
    }
}
