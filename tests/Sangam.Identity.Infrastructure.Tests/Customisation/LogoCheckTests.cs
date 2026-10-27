using System.Text;
using Sangam.Identity.Infrastructure.Customisation;

namespace Sangam.Identity.Infrastructure.Tests.Customisation;

/// <summary>PR-19: a logo is a picture and nothing else. rc.16 (ASVS V1.12.2): only a PNG.</summary>
public sealed class LogoCheckTests
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R', 0, 0, 0, 8];

    [Fact]
    public void APng_IsAccepted_AndTooLargeIsNot()
    {
        Assert.Null(LogoCheck.Problem("image/png", Png, 1024));
        Assert.Contains("at most", LogoCheck.Problem("image/png", new byte[2048], 1024), StringComparison.Ordinal);
        Assert.Contains("PNG", LogoCheck.Problem("image/jpeg", Encoding.UTF8.GetBytes("JFIF"), 1024), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 10 10\"><circle cx=\"5\" cy=\"5\" r=\"4\" fill=\"#1D4E89\"/></svg>")]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>")]
    [InlineData("<html><body>hi</body></html>")]
    public void AnSvg_OrAnythingElse_IsRefused_HoweverItIsLabelled(string text)
    {
        Assert.Equal("Upload the logo as a PNG image.", LogoCheck.Problem("image/svg+xml", Encoding.UTF8.GetBytes(text), 200 * 1024));
        Assert.Equal("Upload the logo as a PNG image.", LogoCheck.Problem("image/png", Encoding.UTF8.GetBytes(text), 200 * 1024));
    }

    [Fact]
    public void ThePngSignature_Alone_IsNotEnough()
    {
        // A file that starts like a PNG but has no image header is not one.
        byte[] fake = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, (byte)'<', (byte)'h', (byte)'t', (byte)'m', (byte)'l', (byte)'>', (byte)'x', (byte)'y'];
        Assert.False(LogoCheck.IsPng(fake));
        Assert.True(LogoCheck.IsPng(Png));
    }
}
