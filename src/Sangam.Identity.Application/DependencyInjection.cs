using Microsoft.Extensions.DependencyInjection;

namespace Sangam.Identity.Application;

/// <summary>Registers the application layer.</summary>
public static class DependencyInjection
{
    /// <summary>
    /// Adds application services. The account use cases are interfaces implemented in
    /// Infrastructure; this is the anchor hosts call so the wiring does not change as use cases grow.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same collection, for chaining.</returns>
    public static IServiceCollection AddSangamApplication(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return services;
    }
}
