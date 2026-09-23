using System.Reflection;

namespace Sangam.Identity.Infrastructure;

/// <summary>
/// Stable handle for the infrastructure assembly, used by EF Core
/// (<c>ApplyConfigurationsFromAssembly</c>, migrations assembly) and by layering tests.
/// </summary>
public static class AssemblyReference
{
    /// <summary>Gets the <c>Sangam.Identity.Infrastructure</c> assembly.</summary>
    public static Assembly Assembly { get; } = typeof(AssemblyReference).Assembly;
}
