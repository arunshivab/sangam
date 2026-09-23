namespace Sangam.Identity.Domain.Entities;

/// <summary>
/// Reference taxonomy of organisation kinds. Seeded by migration; adding a type is a
/// migration, not a runtime operation.
/// </summary>
public sealed class OrgType
{
    /// <summary>Stable lowercase code used as the primary key ("hospital", "department").</summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>Human-readable name shown in consoles.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Optional longer description.</summary>
    public string? Description { get; set; }

    /// <summary>
    /// Whether an organisation of this type may exist without a parent. Corporate groups,
    /// hospitals and clinics can be roots; a department cannot.
    /// </summary>
    public bool CanBeRoot { get; set; } = true;

    /// <summary>Whether organisations of this type may have children (a department cannot).</summary>
    public bool CanHaveChildren { get; set; } = true;

    /// <summary>Ordering in pick lists.</summary>
    public int SortOrder { get; set; }
}
