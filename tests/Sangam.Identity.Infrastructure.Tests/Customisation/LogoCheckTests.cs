using System.Text;
using Sangam.Identity.Infrastructure.Customisation;

namespace Sangam.Identity.Infrastructure.Tests.Customisation;

/// <summary>PR-19: a logo is a picture and nothing else.</summary>
public sealed class LogoCheckTests
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13];

    [Fact]
    public void APng_IsAccepted_AndTooLargeIsNot()
    {
        Assert.Null(LogoCheck.Problem("image/png", Png, 1024));
        Assert.Contains("at most", LogoCheck.Problem("image/png", new byte[2048], 1024), StringComparison.Ordinal);
        Assert.Contains("PNG or an SVG", LogoCheck.Problem("image/jpeg", Encoding.UTF8.GetBytes("JFIF"), 1024), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 10 10\"><circle cx=\"5\" cy=\"5\" r=\"4\" fill=\"#1D4E89\"/><use href=\"#a\"/></svg>", null)]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>", "script")]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\" onload=\"alert(1)\"></svg>", "handler")]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"><foreignObject><div>x</div></foreignObject></svg>", "foreignObject")]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"><image href=\"https://evil.example/x.png\"/></svg>", "outside itself")]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"><a href=\"javascript:alert(1)\"><text>x</text></a></svg>", "outside itself")]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"><style>@import url(https://evil.example/a.css);</style></svg>", "styles")]
    [InlineData("<!DOCTYPE svg [<!ENTITY x \"y\">]><svg xmlns=\"http://www.w3.org/2000/svg\">&x;</svg>", "could not be read")]
    [InlineData("<html><body>hi</body></html>", "not an SVG")]
    public void AnSvg_MustBeOnlyAPicture(string svg, string? problem)
    {
        string? result = LogoCheck.Problem("image/svg+xml", Encoding.UTF8.GetBytes(svg), 200 * 1024);
        if (problem is null)
        {
            Assert.Null(result);
        }
        else
        {
            Assert.Contains(problem, result, StringComparison.Ordinal);
        }
    }
}
