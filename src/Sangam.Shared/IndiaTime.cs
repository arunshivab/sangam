using System.Globalization;

namespace Sangam.Shared;

/// <summary>
/// Every time Sangam shows a person is India Standard Time (V-12): stored instants are UTC, and every screen,
/// e-mail and SMS converts them here, so a console, the portal and a notice never disagree about a date.
/// </summary>
public static class IndiaTime
{
    /// <summary>India Standard Time is UTC+05:30 all year (no daylight saving).</summary>
    public static readonly TimeSpan Offset = TimeSpan.FromHours(5.5);

    /// <summary>The suffix shown after a time of day.</summary>
    public const string Suffix = "IST";

    /// <summary>The instant in India Standard Time.</summary>
    /// <param name="value">The instant.</param>
    public static DateTimeOffset ToIndia(DateTimeOffset value) => value.ToOffset(Offset);

    /// <summary>The instant in India Standard Time with a custom format, in the reader's culture.</summary>
    /// <param name="value">The instant.</param>
    /// <param name="format">A date and time format string.</param>
    public static string Format(DateTimeOffset value, string format)
    {
        ArgumentNullException.ThrowIfNull(format);
        return ToIndia(value).ToString(format, CultureInfo.CurrentCulture);
    }

    /// <summary>The day, for example "7 Oct 2026" — the Indian date, not the UTC one.</summary>
    /// <param name="value">The instant.</param>
    public static string Date(DateTimeOffset value) => Format(value, "d MMM yyyy");

    /// <summary>The day in full, for example "7 October 2026".</summary>
    /// <param name="value">The instant.</param>
    public static string LongDate(DateTimeOffset value) => Format(value, "d MMMM yyyy");

    /// <summary>Day and time, for example "7 Oct 2026, 10:21 IST".</summary>
    /// <param name="value">The instant.</param>
    public static string Stamp(DateTimeOffset value) => Format(value, "d MMM yyyy, HH:mm") + " " + Suffix;

    /// <summary>A short day and time for dense tables, for example "7 Oct, 10:21".</summary>
    /// <param name="value">The instant.</param>
    public static string Compact(DateTimeOffset value) => Format(value, "d MMM, HH:mm");

    /// <summary>The long form used in notices, for example "Thursday, 8 October 2026 7:37 AM IST".</summary>
    /// <param name="value">The instant.</param>
    public static string Full(DateTimeOffset value) => Format(value, "f") + " " + Suffix;
}
