using Sangam.Identity.Domain.Enums;

namespace Sangam.Identity.Domain.Entities;

/// <summary>An uploaded logo (SGM-209 §3), checked and size-limited, served by the identity server.</summary>
public sealed class BrandingAsset
{
    /// <summary>Primary key; part of the address it is served at.</summary>
    public Guid Id { get; set; }

    /// <summary>The level it belongs to.</summary>
    public CustomisationScope Scope { get; set; }

    /// <summary>The application or organisation; null for the platform.</summary>
    public Guid? ScopeId { get; set; }

    /// <summary><c>image/png</c> or <c>image/svg+xml</c>.</summary>
    public string ContentType { get; set; } = string.Empty;

    /// <summary>The image.</summary>
    public byte[] Content { get; set; } = [];

    /// <summary>SHA-256 of the image, hex; used to make the address change when the image does.</summary>
    public string Sha256 { get; set; } = string.Empty;

    /// <summary>Who uploaded it.</summary>
    public Guid CreatedBy { get; set; }

    /// <summary>When it was uploaded.</summary>
    public DateTimeOffset CreatedAt { get; set; }
}
