using Sangam.Shared.Constants;

namespace Sangam.Shared.Tests.Constants;

public sealed class CountryCodesTests
{
    [Fact]
    public void India_IsTheDefaultAndListedFirst()
    {
        Assert.Equal("IN", CountryCodes.DefaultIso);
        Assert.Equal("+91", CountryCodes.DefaultDialCode);
        Assert.Equal("IN", CountryCodes.All[0].Iso);
        Assert.Equal(CountryCodes.All[0], CountryCodes.Default);
        Assert.Equal(10, CountryCodes.Default.NationalDigits);
    }

    [Fact]
    public void EveryEntry_HasAPlusPrefixedNumericDialCodeAndUniqueIso()
    {
        Assert.All(CountryCodes.All, c =>
        {
            Assert.StartsWith("+", c.DialCode, StringComparison.Ordinal);
            Assert.All(c.DialCode[1..], ch => Assert.True(char.IsDigit(ch)));
            Assert.Equal(2, c.Iso.Length);
            Assert.Equal(c.Iso.ToUpperInvariant(), c.Iso);
            Assert.False(string.IsNullOrWhiteSpace(c.Name));
        });

        Assert.Equal(CountryCodes.All.Count, CountryCodes.All.Select(c => c.Iso).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void CountriesAfterIndia_AreAlphabetical()
    {
        string[] names = [.. CountryCodes.All.Skip(1).Select(c => c.Name)];
        Assert.Equal(names.OrderBy(n => n, StringComparer.Ordinal), names);
    }

    [Fact]
    public void OptionLabel_RightAlignsTheCodeSoTheIsoCodesLineUp()
    {
        string india = CountryCodes.OptionLabel(CountryCodes.Default);
        string canada = CountryCodes.OptionLabel(CountryCodes.FindByIso("CA")!);
        string bangladesh = CountryCodes.OptionLabel(CountryCodes.FindByIso("BD")!);

        Assert.Equal(4, CountryCodes.LongestDialCode);
        Assert.Equal("\u00A0+91\u00A0IN", india);
        Assert.Equal("\u00A0\u00A0+1\u00A0CA", canada);
        Assert.Equal("+880\u00A0BD", bangladesh);

        // Every label is the same length, so the ISO codes sit in one column.
        Assert.Single(CountryCodes.All.Select(CountryCodes.OptionLabel).Select(l => l.Length).Distinct());
        Assert.All(CountryCodes.All.Select(CountryCodes.OptionLabel), l => Assert.DoesNotContain(' ', l));
    }

    [Fact]
    public void FindByIso_IsCaseInsensitive_AndNullForUnknown()
    {
        Assert.Equal("+971", CountryCodes.FindByIso("ae")!.DialCode);
        Assert.Equal("+91", CountryCodes.FindByIso("IN")!.DialCode);
        Assert.Null(CountryCodes.FindByIso("ZZ"));
        Assert.Null(CountryCodes.FindByIso(null));
    }
}
