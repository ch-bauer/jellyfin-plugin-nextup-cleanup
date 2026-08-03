using Jellyfin.Plugin.NextUpCleanup.Middleware;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.NextUpCleanup;

/// <summary>
/// Registers the plugin's services with the server's dependency injection container.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.TryAddEnumerable(
            ServiceDescriptor.Transient<IStartupFilter, NextUpStartupFilter>());
    }
}

/// <summary>
/// Puts the filter at the very front of the request pipeline, so it wraps the response
/// the server's own controllers produce — including whatever compression the server
/// applies on the way out.
/// </summary>
internal sealed class NextUpStartupFilter : IStartupFilter
{
    /// <inheritdoc />
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        return app =>
        {
            var logger = app.ApplicationServices.GetRequiredService<ILogger<NextUpFilterMiddleware>>();

            app.Use(async (context, nextMiddleware) =>
            {
                var middleware = new NextUpFilterMiddleware(_ => nextMiddleware(), logger);
                await middleware.InvokeAsync(context).ConfigureAwait(false);
            });

            next(app);
        };
    }
}
