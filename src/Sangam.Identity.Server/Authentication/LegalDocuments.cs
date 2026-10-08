namespace Sangam.Identity.Server.Authentication;

/// <summary>
/// The public documents (PR-13, DEF-025): terms and privacy notice from files the founder supplies
/// after counsel's review (OI-002), and the grievance contact (DPDP Act s.8(9), s.8(10), s.13).
/// Nothing legal is drafted here: until a file is configured the page says the document is pending.
/// </summary>
public static class LegalDocuments
{
    /// <summary>Configuration key: path of the terms file (plain text; blank lines separate paragraphs).</summary>
    public const string TermsPathKey = "Sangam:Legal:TermsPath";

    /// <summary>Configuration key: path of the privacy notice file.</summary>
    public const string PrivacyPathKey = "Sangam:Legal:PrivacyPath";

    /// <summary>Configuration key: name of the grievance officer.</summary>
    public const string OfficerNameKey = "Sangam:Grievance:OfficerName";

    /// <summary>Configuration key: grievance e-mail address (default: the support address).</summary>
    public const string OfficerEmailKey = "Sangam:Grievance:Email";

    /// <summary>Configuration key: postal address for grievances.</summary>
    public const string OfficerAddressKey = "Sangam:Grievance:Address";

    /// <summary>Configuration key: days within which a grievance is answered.</summary>
    public const string ResponseDaysKey = "Sangam:Grievance:ResponseDays";

    /// <summary>Reads a document as paragraphs, or <see langword="null"/> when none is configured.</summary>
    /// <param name="configuration">Configuration.</param>
    /// <param name="key">The path key.</param>
    public static IReadOnlyList<string>? Read(IConfiguration configuration, string key)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        string? path = configuration[key];
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        return File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}
