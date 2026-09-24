namespace Sangam.Shared.Constants;

/// <summary>A country's international dialling code, for the mobile-number field.</summary>
/// <param name="Iso">ISO 3166-1 alpha-2 code.</param>
/// <param name="Name">English name.</param>
/// <param name="DialCode">Dialling code including the leading <c>+</c>.</param>
/// <param name="NationalDigits">Expected number of digits after the dialling code, or 0 when it varies.</param>
public sealed record CountryCode(string Iso, string Name, string DialCode, int NationalDigits);

/// <summary>
/// The dialling codes offered on the registration form. India is the default and is listed
/// first; the rest follow alphabetically. Adding a country is a one-line change here.
/// </summary>
public static class CountryCodes
{
    /// <summary>ISO code of the default country.</summary>
    public const string DefaultIso = "IN";

    /// <summary>Dialling code of the default country.</summary>
    public const string DefaultDialCode = "+91";

    /// <summary>Every offered country, India first, then alphabetical.</summary>
    public static IReadOnlyList<CountryCode> All { get; } =
    [
        new("IN", "India", "+91", 10),
        new("AU", "Australia", "+61", 9),
        new("BH", "Bahrain", "+973", 8),
        new("BD", "Bangladesh", "+880", 10),
        new("BT", "Bhutan", "+975", 8),
        new("BR", "Brazil", "+55", 0),
        new("CA", "Canada", "+1", 10),
        new("CN", "China", "+86", 11),
        new("FR", "France", "+33", 9),
        new("DE", "Germany", "+49", 0),
        new("HK", "Hong Kong", "+852", 8),
        new("ID", "Indonesia", "+62", 0),
        new("IE", "Ireland", "+353", 9),
        new("IL", "Israel", "+972", 9),
        new("IT", "Italy", "+39", 0),
        new("JP", "Japan", "+81", 10),
        new("JO", "Jordan", "+962", 9),
        new("KE", "Kenya", "+254", 9),
        new("KW", "Kuwait", "+965", 8),
        new("MY", "Malaysia", "+60", 0),
        new("MV", "Maldives", "+960", 7),
        new("MU", "Mauritius", "+230", 8),
        new("MM", "Myanmar", "+95", 0),
        new("NP", "Nepal", "+977", 10),
        new("NL", "Netherlands", "+31", 9),
        new("NZ", "New Zealand", "+64", 0),
        new("NG", "Nigeria", "+234", 10),
        new("OM", "Oman", "+968", 8),
        new("PK", "Pakistan", "+92", 10),
        new("PH", "Philippines", "+63", 10),
        new("QA", "Qatar", "+974", 8),
        new("RU", "Russia", "+7", 10),
        new("SA", "Saudi Arabia", "+966", 9),
        new("SG", "Singapore", "+65", 8),
        new("ZA", "South Africa", "+27", 9),
        new("KR", "South Korea", "+82", 0),
        new("LK", "Sri Lanka", "+94", 9),
        new("CH", "Switzerland", "+41", 9),
        new("TZ", "Tanzania", "+255", 9),
        new("TH", "Thailand", "+66", 9),
        new("TR", "Türkiye", "+90", 10),
        new("AE", "United Arab Emirates", "+971", 9),
        new("GB", "United Kingdom", "+44", 10),
        new("US", "United States", "+1", 10),
        new("VN", "Vietnam", "+84", 0),
    ];

    /// <summary>Finds a country by ISO code (case-insensitive), or <see langword="null"/>.</summary>
    /// <param name="iso">ISO 3166-1 alpha-2 code.</param>
    public static CountryCode? FindByIso(string? iso)
        => iso is null ? null : All.FirstOrDefault(c => string.Equals(c.Iso, iso, StringComparison.OrdinalIgnoreCase));

    /// <summary>The default country (India).</summary>
    public static CountryCode Default { get; } = All[0];
}
