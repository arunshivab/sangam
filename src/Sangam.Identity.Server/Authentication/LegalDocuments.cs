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

    /// <summary>Configuration key: days within which a grievance is resolved (D-D: 30).</summary>
    public const string ResponseDaysKey = "Sangam:Grievance:ResponseDays";

    /// <summary>Configuration key: working days within which a grievance is acknowledged (D-D: 2).</summary>
    public const string AcknowledgeDaysKey = "Sangam:Grievance:AcknowledgeWorkingDays";

    /// <summary>Configuration key: who owns and operates Sangam (D-C).</summary>
    public const string OperatorKey = "Sangam:Operator";

    /// <summary>Configuration key: the jurisdiction for the terms (D-C).</summary>
    public const string JurisdictionKey = "Sangam:Jurisdiction";

    /// <summary>Who owns and operates Sangam: configuration, or the founder (D-C).</summary>
    /// <param name="configuration">Configuration.</param>
    public static string Operator(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        string? value = configuration[OperatorKey];
        return string.IsNullOrWhiteSpace(value) ? Sangam.Identity.Domain.PlatformOwner.Name : value;
    }

    /// <summary>The jurisdiction for the terms: configuration, or Ahmedabad (D-C).</summary>
    /// <param name="configuration">Configuration.</param>
    public static string Jurisdiction(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        string? value = configuration[JurisdictionKey];
        return string.IsNullOrWhiteSpace(value) ? Sangam.Identity.Domain.PlatformOwner.Jurisdiction : value;
    }

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
