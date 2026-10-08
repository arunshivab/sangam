using System.Globalization;

namespace Sangam.Shared.Tests;

/// <summary>V-12: every screen shows India Standard Time, including the date of a late-evening UTC instant.</summary>
public sealed class IndiaTimeTests
{
    [Fact]
    public void Instants_AreShownInIst_AndTheDateIsTheIndianDate()
    {
        CultureInfo before = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-IN");
            DateTimeOffset lateUtc = new(2026, 10, 6, 20, 15, 0, TimeSpan.Zero);
            Assert.Equal("7 Oct 2026", IndiaTime.Date(lateUtc));
            Assert.Equal("7 Oct 2026, 01:45 IST", IndiaTime.Stamp(lateUtc));
            Assert.Equal("7 Oct, 01:45", IndiaTime.Compact(lateUtc));
            Assert.Equal("7 October 2026", IndiaTime.LongDate(lateUtc));
            Assert.EndsWith(" IST", IndiaTime.Full(lateUtc), StringComparison.Ordinal);
            Assert.Equal(TimeSpan.FromHours(5.5), IndiaTime.ToIndia(lateUtc).Offset);
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
        }
    }
}
