using System.Reflection;

namespace Sangam.Identity.Domain;

/// <summary>
/// Stable handle for the domain assembly, used by assembly scanning
/// (for example, registering domain event handlers) and by layering tests.
/// </summary>
public static class AssemblyReference
{
    /// <summary>Gets the <c>Sangam.Identity.Domain</c> assembly.</summary>
    public static Assembly Assembly { get; } = typeof(AssemblyReference).Assembly;
}
