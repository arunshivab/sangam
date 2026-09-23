using Microsoft.AspNetCore.Components;

namespace Sangam.Web.Shared.Components;

/// <summary>
/// The Sangam palm-leaf mark: a rounded folio, two incised text rules and the binding cord hole.
/// Renders the authoritative 64 × 64 geometry from the identity system as inline SVG.
/// Minimum on-screen size is 16 px; below that, hosts should use the hinted favicon instead.
/// </summary>
public partial class SangamMark : ComponentBase
{
    private const string Teal = "#0F3B38";
    private const string Ochre = "#8A6A2F";
    private const string Cream = "#F2EFE8";
    private const string Sandalwood = "#D9C9A8";
    private const string LeafMist = "#CBD9D3";
    private const string CurrentColor = "currentColor";

    /// <summary>Rendered width and height in CSS pixels. Defaults to 32.</summary>
    [Parameter]
    public int Size { get; set; } = 32;

    /// <summary>Colour treatment. Defaults to <see cref="SangamMarkMode.Colour"/>.</summary>
    [Parameter]
    public SangamMarkMode Mode { get; set; } = SangamMarkMode.Colour;

    /// <summary>
    /// When <see langword="true"/> the mark is hidden from assistive technology.
    /// Set this whenever the word "sangam" already appears beside the mark (as in a lockup),
    /// so screen readers do not announce the name twice.
    /// </summary>
    [Parameter]
    public bool Decorative { get; set; }

    /// <summary>Additional CSS classes appended after <c>sg-mark</c>.</summary>
    [Parameter]
    public string? Class { get; set; }

    private string CssClass => string.IsNullOrWhiteSpace(Class) ? "sg-mark" : "sg-mark " + Class;

    private string FolioColour => Mode switch
    {
        SangamMarkMode.Reversed => Cream,
        SangamMarkMode.Mono => CurrentColor,
        SangamMarkMode.LeafMist => LeafMist,
        _ => Teal,
    };

    private string HoleColour => Mode switch
    {
        SangamMarkMode.Reversed => Sandalwood,
        SangamMarkMode.Mono => CurrentColor,
        SangamMarkMode.LeafMist => LeafMist,
        _ => Ochre,
    };
}
