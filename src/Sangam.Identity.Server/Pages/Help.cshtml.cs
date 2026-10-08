using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Sangam.Identity.Server.Pages;

/// <summary>The help page (PR-13, DEF-025).</summary>
public sealed class HelpModel : PageModel
{
    private readonly IConfiguration _configuration;

    /// <summary>Initialises the page.</summary>
    /// <param name="configuration">Configuration.</param>
    public HelpModel(IConfiguration configuration)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
    }

    /// <summary>The support address.</summary>
    public string SupportEmail => _configuration["Sangam:SupportEmail"] ?? "help@sangamid.in";
}
