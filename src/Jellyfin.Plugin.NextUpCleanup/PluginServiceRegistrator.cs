using Jellyfin.Plugin.NextUpCleanup.Filtering;
using Jellyfin.Plugin.NextUpCleanup.Tasks;
using Jellyfin.Plugin.NextUpCleanup.Web;
using Microsoft.AspNetCore.Hosting;
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

        // index.html is a static file with no server-side hook, so the series-toggle
        // script goes in via middleware, the way Jellyfin Enhanced does it. Registered as
        // a singleton, deliberately: the same filter added with a transient descriptor
        // disturbed startup enough to trip a disposed-provider bug in the File
        // Transformation plugin, which took index.html down with it.
        serviceCollection.AddSingleton<IStartupFilter, ScriptInjectionStartupFilter>();

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
