using LiPicons.Blazor;

namespace Sangam.Web.Shared.Tests.Components;

/// <summary>Proves the LiPicons package restores from localpackages/ and renders with no network.</summary>
public sealed class LipiIconTests
{
    [Fact]
    public async Task Lock_RendersInlineSvgWithAccessibleTitle()
    {
        string html = await ComponentTestHost.RenderAsync<LipiIcon>(new Dictionary<string, object?>
        {
            [nameof(LipiIcon.Name)] = LipiconName.Lock,
            [nameof(LipiIcon.Size)] = 24,
            [nameof(LipiIcon.Title)] = "Password",
        });

        Assert.Contains("<svg", html, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"Password\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("href=\"http", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("src=\"http", html, StringComparison.OrdinalIgnoreCase);
    }
}
