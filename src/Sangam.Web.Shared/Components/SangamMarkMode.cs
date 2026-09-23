namespace Sangam.Web.Shared.Components;

/// <summary>
/// Colour treatments of the palm-leaf mark permitted by the Sangam identity system.
/// Anything else (off-palette recolouring, gradients, shadows) is a brand misuse.
/// </summary>
public enum SangamMarkMode
{
    /// <summary>Teal folio and ochre cord hole — the primary mark on light surfaces.</summary>
    Colour = 0,

    /// <summary>Cream folio and Sandalwood cord hole — reversed on teal or the admin chrome.</summary>
    Reversed = 1,

    /// <summary>Single colour: both folio and hole take <c>currentColor</c>.</summary>
    Mono = 2,

    /// <summary>Leaf Mist folio and hole — reserved for empty states only.</summary>
    LeafMist = 3,
}
