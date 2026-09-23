using System.Reflection;

namespace Sangam.Identity.Application.Tests;

/// <summary>
/// The application layer may know the domain and shared kernel, never a provider or a host.
/// </summary>
public sealed class LayeringTests
{
    private static readonly string[] ForbiddenPrefixes =
    [
        "Microsoft.EntityFrameworkCore",
        "Microsoft.AspNetCore",
        "Npgsql",
        "OpenIddict",
        "Sangam.Identity.Infrastructure",
        "Sangam.Identity.Server",
        "Sangam.Admin.Web",
        "Sangam.SelfService.Web",
        "Sangam.Web.Shared",
        "Sangam.Client",
    ];

    [Fact]
    public void AssemblyReference_PointsAtApplicationAssembly()
    {
        Assert.Equal("Sangam.Identity.Application", AssemblyReference.Assembly.GetName().Name);
    }

    [Fact]
    public void Application_ReferencesNoProviderOrHost()
    {
        AssemblyName[] references = AssemblyReference.Assembly.GetReferencedAssemblies();

        foreach (AssemblyName reference in references)
        {
            string name = reference.Name ?? string.Empty;
            foreach (string forbidden in ForbiddenPrefixes)
            {
                Assert.False(
                    name.StartsWith(forbidden, StringComparison.Ordinal),
                    $"Sangam.Identity.Application must not reference {name}.");
            }
        }
    }
}
