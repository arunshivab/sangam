using Microsoft.AspNetCore.Components;

namespace Sangam.Web.Shared.Components;

/// <summary>
/// rc.10: the strip across the top of the operator and partner consoles — the environment (red anywhere but
/// production), how this session was signed in, when it ends, and "Sign in again" (a fresh two-step sign-in).
/// </summary>
public partial class ConsoleStrip : ComponentBase
{
    /// <summary>The host environment's name.</summary>
    [Parameter]
    [EditorRequired]
    public string EnvironmentName { get; set; } = string.Empty;

    /// <summary>The environment as shown, for example <c>PRODUCTION · India</c>.</summary>
    [Parameter]
    [EditorRequired]
    public string EnvironmentLabel { get; set; } = string.Empty;

    /// <summary>How strongly this session was signed in: 2 two-step sign-in, 3 a passkey.</summary>
    [Parameter]
    public int SignInLevel { get; set; }

    /// <summary>When the session ends at the latest.</summary>
    [Parameter]
    public DateTimeOffset? SessionEndsAt { get; set; }

    private string ReauthUrl => "/reauth?returnUrl=" + Uri.EscapeDataString("/" + Navigation.ToBaseRelativePath(Navigation.Uri));
}
