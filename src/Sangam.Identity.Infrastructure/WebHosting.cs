using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
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

    /// <summary>Configuration key: OTLP collector endpoint. Unset means no telemetry is exported.</summary>
    public const string OtlpEndpointKey = "Sangam:Telemetry:OtlpEndpoint";

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

        // Observability (PR-10, SGM-307): traces and metrics over OTLP to any collector, only when
        // an endpoint is configured. The collector and dashboards are the founder's choice.
        string? otlp = configuration[OtlpEndpointKey];
        if (!string.IsNullOrWhiteSpace(otlp))
        {
            Uri endpoint = new(otlp, UriKind.Absolute);
            string serviceName = configuration["Sangam:Telemetry:ServiceName"] ?? System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name ?? "sangam";
            services.AddOpenTelemetry()
                .ConfigureResource(r => r.AddService(serviceName))
                .WithTracing(t => t.AddAspNetCoreInstrumentation().AddHttpClientInstrumentation().AddOtlpExporter(o => o.Endpoint = endpoint))
                .WithMetrics(m => m.AddAspNetCoreInstrumentation().AddHttpClientInstrumentation().AddOtlpExporter(o => o.Endpoint = endpoint));
        }

        return services;
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
