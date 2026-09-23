using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.Web.HtmlRendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Sangam.Web.Shared.Tests.Components;

/// <summary>
/// Renders a component to static HTML using the framework's <see cref="HtmlRenderer"/>,
/// so the brand components can be asserted without a browser or a test-specific library.
/// </summary>
internal static class ComponentTestHost
{
    public static async Task<string> RenderAsync<TComponent>(IDictionary<string, object?> parameters)
        where TComponent : IComponent
    {
        ServiceCollection services = new();
        services.AddLogging();
        await using ServiceProvider provider = services.BuildServiceProvider();
        ILoggerFactory loggerFactory = provider.GetRequiredService<ILoggerFactory>();

        await using HtmlRenderer renderer = new(provider, loggerFactory);
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            HtmlRootComponent root = await renderer.RenderComponentAsync<TComponent>(ParameterView.FromDictionary(parameters)).ConfigureAwait(false);
            return root.ToHtmlString();
        }).ConfigureAwait(false);
    }
}
