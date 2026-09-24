using Microsoft.AspNetCore.Mvc.RazorPages;
using Sangam.Identity.Infrastructure.Seeding;

namespace Sangam.Identity.Server.Pages;

/// <summary>
/// PR-01 foundation page: renders the tokens, brand CSS and palm-leaf components in the
/// Razor Pages host so the design system can be verified before the auth screens land.
/// </summary>
public sealed class IndexModel : PageModel
{
    private readonly IConfiguration _configuration;
    private readonly IWebHostEnvironment _environment;

    /// <summary>Initialises the page with the application configuration.</summary>
    /// <param name="configuration">Application configuration (the <c>Sangam</c> section is read).</param>
    /// <param name="environment">Host environment.</param>
    public IndexModel(IConfiguration configuration, IWebHostEnvironment environment)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
    }

    /// <summary>Gets the public host name this server answers on.</summary>
    public string Host { get; private set; } = string.Empty;

    /// <summary>Gets the legal operator named in the footer of every auth screen.</summary>
    public string Operator { get; private set; } = string.Empty;

    /// <summary>Gets the informational version of the running assembly.</summary>
    public string Version { get; private set; } = string.Empty;

    /// <summary>Whether to offer the development partner-flow link.</summary>
    public bool ShowDevFlowLink { get; private set; }

    /// <summary>A ready-made authorization request for the seeded sample app.</summary>
    public string DevFlowUrl { get; private set; } = string.Empty;

    /// <summary>Populates the page from configuration.</summary>
    public void OnGet()
    {
        ShowDevFlowLink = _environment.IsDevelopment();
        if (ShowDevFlowLink)
        {
            // Redirect back to this same origin, so the code is issued and exchanged under one issuer.
            string callback = $"{Request.Scheme}://{Request.Host}/dev/callback";
            DevFlowUrl = "/connect/authorize?client_id=" + DevelopmentSeeder.SampleClientId
                + "&redirect_uri=" + Uri.EscapeDataString(callback)
                + "&response_type=code&scope=" + Uri.EscapeDataString("openid profile email phone orgs.read offline_access")
                + "&state=demo&code_challenge=" + DevelopmentSeeder.DevCallbackChallenge
                + "&code_challenge_method=S256";
        }

        Host = _configuration["Sangam:Host"] ?? "id.sangamid.in";
        Operator = _configuration["Sangam:Operator"] ?? "imagiQa Healthcare Services Pvt Ltd";
        Version = (typeof(IndexModel).Assembly.GetName().Version ?? new Version(0, 0, 0)).ToString(3);
    }
}
