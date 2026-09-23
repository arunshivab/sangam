using Sangam.Web.Shared.Components;

namespace Sangam.Web.Shared.Tests.Components;

public sealed class SangamLockupTests
{
    [Fact]
    public async Task Primary_RendersLowercaseWordmarkWithDecorativeMark()
    {
        string html = await ComponentTestHost.RenderAsync<SangamLockup>(new Dictionary<string, object?>());

        Assert.Contains("class=\"sg-lockup sg-lockup--colour\"", html, StringComparison.Ordinal);
        Assert.Contains("--sg-lockup-mark: 26px", html, StringComparison.Ordinal);
        Assert.Contains("<span class=\"sg-wordmark\">sangam</span>", html, StringComparison.Ordinal);
        Assert.Contains("aria-hidden=\"true\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("sg-lockup-id", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Sangam</span>", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Functional_AppendsIdSuffix()
    {
        string html = await ComponentTestHost.RenderAsync<SangamLockup>(new Dictionary<string, object?>
        {
            [nameof(SangamLockup.Functional)] = true,
        });

        Assert.Contains("<span class=\"sg-lockup-id\" aria-label=\"ID\">ID</span>", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reversed_PropagatesModeToMarkAndClass()
    {
        string html = await ComponentTestHost.RenderAsync<SangamLockup>(new Dictionary<string, object?>
        {
            [nameof(SangamLockup.Mode)] = SangamMarkMode.Reversed,
            [nameof(SangamLockup.MarkSize)] = 24,
        });

        Assert.Contains("sg-lockup--reversed", html, StringComparison.Ordinal);
        Assert.Contains("--sg-lockup-mark: 24px", html, StringComparison.Ordinal);
        Assert.Contains("stroke=\"#F2EFE8\"", html, StringComparison.Ordinal);
    }
}
