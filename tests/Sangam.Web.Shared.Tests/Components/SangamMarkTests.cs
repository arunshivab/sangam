using Sangam.Web.Shared.Components;

namespace Sangam.Web.Shared.Tests.Components;

public sealed class SangamMarkTests
{
    [Fact]
    public async Task Default_RendersAccessibleTealMarkAtThirtyTwoPixels()
    {
        string html = await ComponentTestHost.RenderAsync<SangamMark>(new Dictionary<string, object?>());

        Assert.Contains("role=\"img\"", html, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"Sangam\"", html, StringComparison.Ordinal);
        Assert.Contains("width=\"32\"", html, StringComparison.Ordinal);
        Assert.Contains("stroke=\"#0F3B38\"", html, StringComparison.Ordinal);
        Assert.Contains("fill=\"#8A6A2F\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("aria-hidden", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reversed_UsesCreamFolioAndSandalwoodHole()
    {
        string html = await ComponentTestHost.RenderAsync<SangamMark>(new Dictionary<string, object?>
        {
            [nameof(SangamMark.Mode)] = SangamMarkMode.Reversed,
        });

        Assert.Contains("stroke=\"#F2EFE8\"", html, StringComparison.Ordinal);
        Assert.Contains("fill=\"#D9C9A8\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Mono_UsesCurrentColorForBothShapes()
    {
        string html = await ComponentTestHost.RenderAsync<SangamMark>(new Dictionary<string, object?>
        {
            [nameof(SangamMark.Mode)] = SangamMarkMode.Mono,
        });

        Assert.Contains("stroke=\"currentColor\"", html, StringComparison.Ordinal);
        Assert.Contains("fill=\"currentColor\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("#0F3B38", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Decorative_HidesFromAssistiveTechnology()
    {
        string html = await ComponentTestHost.RenderAsync<SangamMark>(new Dictionary<string, object?>
        {
            [nameof(SangamMark.Decorative)] = true,
            [nameof(SangamMark.Size)] = 16,
        });

        Assert.Contains("aria-hidden=\"true\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("role=\"img\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("aria-label", html, StringComparison.Ordinal);
        Assert.Contains("height=\"16\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Geometry_MatchesTheIdentitySystem()
    {
        string html = await ComponentTestHost.RenderAsync<SangamMark>(new Dictionary<string, object?>());

        Assert.Contains("viewBox=\"0 0 64 64\"", html, StringComparison.Ordinal);
        Assert.Contains("rx=\"15.75\"", html, StringComparison.Ordinal);
        Assert.Contains("stroke-width=\"5.5\"", html, StringComparison.Ordinal);
        Assert.Contains("d=\"M33 26.5 H51\"", html, StringComparison.Ordinal);
        Assert.Contains("d=\"M33 37.5 H51\"", html, StringComparison.Ordinal);
        Assert.Contains("cx=\"20\" cy=\"32\" r=\"6.25\"", html, StringComparison.Ordinal);
    }
}
