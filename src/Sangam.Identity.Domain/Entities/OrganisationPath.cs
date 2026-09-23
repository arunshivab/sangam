using System.Globalization;

namespace Sangam.Identity.Domain.Entities;

/// <summary>
/// Helpers for the materialised <see cref="Organisation.Path"/>:
/// <c>/{root}/{child}/…/{self}/</c>, ids in lowercase "D" format.
/// </summary>
public static class OrganisationPath
{
    /// <summary>Path separator.</summary>
    public const char Separator = '/';

    /// <summary>Builds the path for a root organisation.</summary>
    /// <param name="id">The organisation's id.</param>
    /// <returns><c>/{id}/</c>.</returns>
    public static string ForRoot(Guid id) => string.Create(CultureInfo.InvariantCulture, $"{Separator}{id:D}{Separator}");

    /// <summary>Builds the path for a child under <paramref name="parentPath"/>.</summary>
    /// <param name="parentPath">The parent's materialised path (must end with the separator).</param>
    /// <param name="id">The child's id.</param>
    /// <returns><c>{parentPath}{id}/</c>.</returns>
    public static string ForChild(string parentPath, Guid id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parentPath);
        if (parentPath[^1] != Separator)
        {
            throw new ArgumentException("A parent path must end with the separator.", nameof(parentPath));
        }

        return string.Create(CultureInfo.InvariantCulture, $"{parentPath}{id:D}{Separator}");
    }

    /// <summary>Whether <paramref name="candidate"/> is <paramref name="ancestorPath"/> itself or lies beneath it.</summary>
    /// <param name="ancestorPath">Path of the suspected ancestor.</param>
    /// <param name="candidate">Path to test.</param>
    /// <returns><see langword="true"/> when the candidate is the ancestor or a descendant of it.</returns>
    public static bool IsSelfOrDescendant(string ancestorPath, string candidate)
    {
        ArgumentNullException.ThrowIfNull(ancestorPath);
        ArgumentNullException.ThrowIfNull(candidate);
        return candidate.StartsWith(ancestorPath, StringComparison.Ordinal);
    }

    /// <summary>Extracts the ids along the path, root first.</summary>
    /// <param name="path">A materialised path.</param>
    /// <returns>The ids in order.</returns>
    public static IReadOnlyList<Guid> Ids(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        string[] parts = path.Split(Separator, StringSplitOptions.RemoveEmptyEntries);
        Guid[] ids = new Guid[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            ids[i] = Guid.ParseExact(parts[i], "D");
        }

        return ids;
    }
}
