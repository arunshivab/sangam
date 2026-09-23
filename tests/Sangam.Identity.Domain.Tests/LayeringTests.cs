using System.Reflection;

namespace Sangam.Identity.Domain.Tests;

/// <summary>
/// Guards the load-bearing wall of the clean architecture: the domain assembly
/// depends on nothing but the BCL and Sangam.Shared.
/// </summary>
public sealed class LayeringTests
{
    private static readonly string[] ForbiddenPrefixes =
    [
        "Microsoft.EntityFrameworkCore",
        "Microsoft.AspNetCore",
        "Npgsql",
        "OpenIddict",
        "Sangam.Identity.Application",
        "Sangam.Identity.Infrastructure",
        "Sangam.Identity.Server",
        "Sangam.Admin.Web",
        "Sangam.SelfService.Web",
        "Sangam.Web.Shared",
        "Sangam.Client",
    ];

    [Fact]
    public void AssemblyReference_PointsAtDomainAssembly()
    {
        Assert.Equal("Sangam.Identity.Domain", AssemblyReference.Assembly.GetName().Name);
    }

    [Fact]
    public void Domain_ReferencesNoOuterLayerOrProvider()
    {
        AssemblyName[] references = AssemblyReference.Assembly.GetReferencedAssemblies();

        foreach (AssemblyName reference in references)
        {
            string name = reference.Name ?? string.Empty;
            foreach (string forbidden in ForbiddenPrefixes)
            {
                Assert.False(
                    name.StartsWith(forbidden, StringComparison.Ordinal),
                    $"Sangam.Identity.Domain must not reference {name}.");
            }
        }
    }
}
