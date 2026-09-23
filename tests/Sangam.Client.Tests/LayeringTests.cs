using System.Reflection;

namespace Sangam.Client.Tests;

/// <summary>
/// The partner SDK ships only the shared kernel — never server-side layers or web assets.
/// </summary>
public sealed class LayeringTests
{
    private static readonly string[] ForbiddenPrefixes =
    [
        "Sangam.Identity",
        "Sangam.Admin.Web",
        "Sangam.SelfService.Web",
        "Sangam.Web.Shared",
        "Microsoft.EntityFrameworkCore",
        "Npgsql",
        "OpenIddict",
    ];

    [Fact]
    public void AssemblyReference_PointsAtClientAssembly()
    {
        Assert.Equal("Sangam.Client", AssemblyReference.Assembly.GetName().Name);
    }

    [Fact]
    public void Client_ReferencesOnlySharedKernel()
    {
        AssemblyName[] references = AssemblyReference.Assembly.GetReferencedAssemblies();

        foreach (AssemblyName reference in references)
        {
            string name = reference.Name ?? string.Empty;
            foreach (string forbidden in ForbiddenPrefixes)
            {
                Assert.False(
                    name.StartsWith(forbidden, StringComparison.Ordinal),
                    $"Sangam.Client must not reference {name}.");
            }
        }
    }
}
