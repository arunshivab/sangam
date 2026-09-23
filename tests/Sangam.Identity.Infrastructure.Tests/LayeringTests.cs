using System.Reflection;

namespace Sangam.Identity.Infrastructure.Tests;

/// <summary>
/// Infrastructure implements the application abstractions; it never depends on a host.
/// </summary>
public sealed class LayeringTests
{
    private static readonly string[] ForbiddenPrefixes =
    [
        "Sangam.Identity.Server",
        "Sangam.Admin.Web",
        "Sangam.SelfService.Web",
        "Sangam.Web.Shared",
        "Sangam.Client",
    ];

    [Fact]
    public void AssemblyReference_PointsAtInfrastructureAssembly()
    {
        Assert.Equal("Sangam.Identity.Infrastructure", AssemblyReference.Assembly.GetName().Name);
    }

    [Fact]
    public void Infrastructure_ReferencesNoHost()
    {
        AssemblyName[] references = AssemblyReference.Assembly.GetReferencedAssemblies();

        foreach (AssemblyName reference in references)
        {
            string name = reference.Name ?? string.Empty;
            foreach (string forbidden in ForbiddenPrefixes)
            {
                Assert.False(
                    name.StartsWith(forbidden, StringComparison.Ordinal),
                    $"Sangam.Identity.Infrastructure must not reference {name}.");
            }
        }
    }
}
