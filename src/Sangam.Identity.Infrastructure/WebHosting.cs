using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
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
        return services;
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
