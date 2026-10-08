using Microsoft.AspNetCore.Mvc.RazorPages;
using Sangam.Identity.Server.Authentication;

namespace Sangam.Identity.Server.Pages;

/// <summary>The privacy notice page (PR-13, DEF-025): the founder's file, or a pending notice (OI-002).</summary>
public sealed class PrivacyModel : PageModel
{
    private readonly IConfiguration _configuration;

    /// <summary>Initialises the page.</summary>
    /// <param name="configuration">Configuration.</param>
    public PrivacyModel(IConfiguration configuration)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
    }

    /// <summary>The document's paragraphs, or <see langword="null"/> while it is pending.</summary>
    public IReadOnlyList<string>? Paragraphs { get; private set; }

    /// <summary>Who owns and operates Sangam (D-C).</summary>
    public string Operator => LegalDocuments.Operator(_configuration);

    /// <summary>The support address.</summary>
    public string SupportEmail => _configuration["Sangam:SupportEmail"] ?? "help@sangamid.in";

    /// <summary>Loads the document.</summary>
    public void OnGet()
    {
        Paragraphs = LegalDocuments.Read(_configuration, LegalDocuments.PrivacyPathKey);
    }
}
