using Microsoft.AspNetCore.Mvc.RazorPages;
using Sangam.Identity.Server.Authentication;

namespace Sangam.Identity.Server.Pages;

/// <summary>
/// The grievance page (PR-13, REQ-089; DPDP Act s.8(9), s.8(10), s.13). Who the officer is, and the
/// response time, are the founder's to configure; nothing is shown that has not been decided.
/// </summary>
public sealed class GrievanceModel : PageModel
{
    private readonly IConfiguration _configuration;

    /// <summary>Initialises the page.</summary>
    /// <param name="configuration">Configuration.</param>
    public GrievanceModel(IConfiguration configuration)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
    }

    /// <summary>The grievance officer, once named.</summary>
    public string? OfficerName => Value(LegalDocuments.OfficerNameKey);

    /// <summary>Where to write.</summary>
    public string Email => Value(LegalDocuments.OfficerEmailKey) ?? Value("Sangam:SupportEmail") ?? "help@sangamid.in";

    /// <summary>Postal address, if published.</summary>
    public string? Address => Value(LegalDocuments.OfficerAddressKey);

    /// <summary>Response time in days, once decided.</summary>
    public string? ResponseDays => Value(LegalDocuments.ResponseDaysKey);

    private string? Value(string key)
    {
        string? value = _configuration[key];
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
