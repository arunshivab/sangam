using System.Reflection;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Sangam.Identity.Infrastructure.Seeding;

namespace Sangam.Identity.Server.Pages;

/// <summary>
/// The identity server's front door (rc.5; it replaces the PR-01 design-system check): what Sangam is, the way to the
/// account portal, and how to report a security problem. In Development it also offers the sample partner sign-in.
/// </summary>
public sealed class IndexModel : PageModel
{
    private readonly IWebHostEnvironment _environment;

    /// <summary>Initialises the page.</summary>
    /// <param name="environment">Host environment.</param>
    public IndexModel(IWebHostEnvironment environment)
    {
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
    }

    /// <summary>Gets the informational version of the running assembly.</summary>
    public string Version { get; private set; } = string.Empty;

    /// <summary>Whether to offer the development partner-flow link.</summary>
    public bool ShowDevFlowLink { get; private set; }

    /// <summary>A ready-made authorization request for the seeded sample app.</summary>
    public string DevFlowUrl { get; private set; } = string.Empty;

    /// <summary>Populates the page.</summary>
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

        // The full version, release candidate included (1.0.0-rc.1), without the commit after "+".
        Version = typeof(IndexModel).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";
    }
}
