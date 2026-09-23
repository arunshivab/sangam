using Microsoft.AspNetCore.Components;

namespace Sangam.Web.Shared.Components;

/// <summary>
/// The Sangam lockup: palm-leaf mark followed by the lowercase <c>sangam</c> wordmark
/// (Newsreader 500). With <see cref="Functional"/> set, the <c>ID</c> suffix in Mukta 600
/// is appended for consent, console and documentation surfaces.
/// The wordmark and gap scale from <see cref="MarkSize"/> via CSS custom properties
/// declared in <c>sangam-brand.css</c>. Minimum width of the primary lockup is 96 px.
/// </summary>
public partial class SangamLockup : ComponentBase
{
    /// <summary>Height of the mark in CSS pixels; the wordmark scales from it. Defaults to 26 (auth screens).</summary>
    [Parameter]
    public int MarkSize { get; set; } = 26;

    /// <summary>Colour treatment applied to mark and wordmark together.</summary>
    [Parameter]
    public SangamMarkMode Mode { get; set; } = SangamMarkMode.Colour;

    /// <summary>When <see langword="true"/>, renders the functional lockup with the ochre <c>ID</c> suffix.</summary>
    [Parameter]
    public bool Functional { get; set; }

    /// <summary>Additional CSS classes appended after the lockup classes.</summary>
    [Parameter]
    public string? Class { get; set; }

    private string CssClass
    {
        get
        {
            string mode = Mode switch
            {
                SangamMarkMode.Reversed => "sg-lockup--reversed",
                SangamMarkMode.Mono => "sg-lockup--mono",
                SangamMarkMode.LeafMist => "sg-lockup--leaf-mist",
                _ => "sg-lockup--colour",
            };

            string classes = "sg-lockup " + mode;
            return string.IsNullOrWhiteSpace(Class) ? classes : classes + " " + Class;
        }
    }
}
