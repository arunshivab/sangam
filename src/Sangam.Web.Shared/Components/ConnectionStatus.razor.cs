using Microsoft.AspNetCore.Components;

namespace Sangam.Web.Shared.Components;

/// <summary>
/// rc.2: tells the person when an interactive console loses its live connection, and offers to try again or reload,
/// instead of buttons that silently stop working. It also stands in for Blazor's error box when a page fails. Place it
/// once in <c>App.razor</c>, outside the interactive root, before <c>blazor.web.js</c>.
/// </summary>
public partial class ConnectionStatus : ComponentBase
{
}
