using System.Reflection;

namespace Sangam.Client;

/// <summary>
/// Stable handle for the client SDK assembly, used by diagnostics
/// (reporting the SDK version to the server) and by layering tests.
/// </summary>
public static class AssemblyReference
{
    /// <summary>Gets the <c>Sangam.Client</c> assembly.</summary>
    public static Assembly Assembly { get; } = typeof(AssemblyReference).Assembly;
}
