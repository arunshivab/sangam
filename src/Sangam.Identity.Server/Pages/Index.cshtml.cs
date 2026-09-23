using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Sangam.Identity.Server.Pages;

/// <summary>
/// PR-01 foundation page: renders the tokens, brand CSS and palm-leaf components in the
/// Razor Pages host so the design system can be verified before the auth screens land.
/// </summary>
public sealed class IndexModel : PageModel
{
    private readonly IConfiguration _configuration;

    /// <summary>Initialises the page with the application configuration.</summary>
    /// <param name="configuration">Application configuration (the <c>Sangam</c> section is read).</param>
    public IndexModel(IConfiguration configuration)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
    }

    /// <summary>Gets the public host name this server answers on.</summary>
    public string Host { get; private set; } = string.Empty;

    /// <summary>Gets the legal operator named in the footer of every auth screen.</summary>
    public string Operator { get; private set; } = string.Empty;

    /// <summary>Gets the informational version of the running assembly.</summary>
    public string Version { get; private set; } = string.Empty;

    /// <summary>Populates the page from configuration.</summary>
    public void OnGet()
    {
        Host = _configuration["Sangam:Host"] ?? "id.sangamid.in";
        Operator = _configuration["Sangam:Operator"] ?? "imagiQa Healthcare Services Pvt Ltd";
        Version = (typeof(IndexModel).Assembly.GetName().Version ?? new Version(0, 0, 0)).ToString(3);
    }
}
