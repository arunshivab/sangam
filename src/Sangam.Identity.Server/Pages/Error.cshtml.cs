using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Sangam.Identity.Server.Pages;

/// <summary>Generic error page; shows a trace reference and never the exception.</summary>
[ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
public sealed class ErrorModel : PageModel
{
    /// <summary>Correlation id for support.</summary>
    public string RequestId { get; private set; } = string.Empty;

    /// <summary>Renders the page.</summary>
    public void OnGet()
    {
        RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier;
    }
}
