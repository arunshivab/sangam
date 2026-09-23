using System.Reflection;

namespace Sangam.Identity.Application;

/// <summary>
/// Stable handle for the application assembly, used by assembly scanning
/// (handlers, validators) and by layering tests.
/// </summary>
public static class AssemblyReference
{
    /// <summary>Gets the <c>Sangam.Identity.Application</c> assembly.</summary>
    public static Assembly Assembly { get; } = typeof(AssemblyReference).Assembly;
}
