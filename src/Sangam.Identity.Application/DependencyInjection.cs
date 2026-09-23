using Microsoft.Extensions.DependencyInjection;

namespace Sangam.Identity.Application;

/// <summary>Registers the application layer.</summary>
public static class DependencyInjection
{
    /// <summary>
    /// Adds application services. Use cases arrive from PR-03; in PR-02 this is the anchor
    /// that hosts call so the wiring does not change when they do.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same collection, for chaining.</returns>
    public static IServiceCollection AddSangamApplication(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return services;
    }
}
