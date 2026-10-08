using Sangam.Identity.Domain.Enums;

namespace Sangam.Identity.Domain.Entities;

/// <summary>A customisation setting at one level (SGM-209 §8): for example the sign-in page branding of an application.</summary>
public sealed class CustomisationSetting
{
    /// <summary>Primary key.</summary>
    public Guid Id { get; set; }

    /// <summary>The level it applies at.</summary>
    public CustomisationScope Scope { get; set; }

    /// <summary>The application or organisation; null for the platform.</summary>
    public Guid? ScopeId { get; set; }

    /// <summary>What is customised, for example <c>branding</c>.</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>The setting, as JSON.</summary>
    public string Value { get; set; } = "{}";

    /// <summary>Incremented on every change.</summary>
    public int Version { get; set; }

    /// <summary>Who changed it last.</summary>
    public Guid? UpdatedBy { get; set; }

    /// <summary>When it was changed last.</summary>
    public DateTimeOffset UpdatedAt { get; set; }
}
