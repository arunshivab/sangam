using Microsoft.Extensions.Configuration;
using Sangam.Identity.Infrastructure.Admin;

namespace Sangam.Identity.Infrastructure.Tests.Admin;

/// <summary>V-13: no setting can bring the support-reset cooling-off below D-K's 24 hours.</summary>
public sealed class CoolingOffTests
{
    [Theory]
    [InlineData(null, null, 24, 72)]
    [InlineData("1", null, 24, 72)]
    [InlineData("0", "1", 24, 24)]
    [InlineData("-5", "12", 24, 24)]
    [InlineData("36", null, 36, 72)]
    [InlineData("96", "72", 96, 96)]
    [InlineData("24", "120", 24, 120)]
    public void TheCoolingOff_IsNeverBelow24Hours_AndPrivilegedIsNeverShorter(string? ordinary, string? privileged, int expectedOrdinary, int expectedPrivileged)
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Sangam:Recovery:CoolingOffHours"] = ordinary,
            ["Sangam:Recovery:PrivilegedCoolingOffHours"] = privileged,
        }).Build();

        Assert.Equal(TimeSpan.FromHours(expectedOrdinary), EfMfaResetService.CoolingOff(configuration, privileged: false));
        Assert.Equal(TimeSpan.FromHours(expectedPrivileged), EfMfaResetService.CoolingOff(configuration, privileged: true));
    }
}
