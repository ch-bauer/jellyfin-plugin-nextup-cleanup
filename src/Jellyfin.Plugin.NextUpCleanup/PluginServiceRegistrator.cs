using Jellyfin.Plugin.NextUpCleanup.Filtering;
using Jellyfin.Plugin.NextUpCleanup.Tasks;
using Jellyfin.Plugin.NextUpCleanup.Web;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.NextUpCleanup;

/// <summary>
/// Registers the plugin's services with the server's dependency injection container.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<SeriesExclusionStore>();
        serviceCollection.AddSingleton<NextUpActionFilter>();

        // index.html is a static file, so the series toggle's script has to be injected
        // by middleware rather than hooked like a controller result.
        serviceCollection.TryAddEnumerable(
            ServiceDescriptor.Transient<IStartupFilter, ScriptInjectionStartupFilter>());

        // The server discovers IScheduledTask across plugin assemblies itself; registering
        // the type is what lets it be constructed with its dependencies.
        serviceCollection.AddTransient<ResetAbandonedEpisodesTask>();

        // A global MVC filter, so it sees every controller the server dispatches to —
        // Jellyfin's own and those that plugins add, which is how rows built by the Home
        // Screen Sections plugin get filtered without it knowing about this plugin.
        serviceCollection.Configure<MvcOptions>(options =>
        {
            options.Filters.AddService<NextUpActionFilter>();
        });
    }
}
