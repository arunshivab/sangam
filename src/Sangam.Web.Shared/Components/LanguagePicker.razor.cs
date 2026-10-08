using Microsoft.AspNetCore.Components;

namespace Sangam.Web.Shared.Components;

/// <summary>
/// The language picker (PR-18): each offered language in its own script, the current one marked. Choosing one sets
/// the culture cookie and reloads the same page in full, so an interactive console picks the language up too.
/// </summary>
public partial class LanguagePicker : ComponentBase
{
    /// <summary>The page to come back to (path and query); defaults to the current page.</summary>
    [Parameter]
    public string? ReturnUrl { get; set; }

    /// <summary>Additional CSS classes.</summary>
    [Parameter]
    public string? Class { get; set; }

    private string Here => ReturnUrl ?? "/" + Navigation.ToBaseRelativePath(Navigation.Uri);
}
