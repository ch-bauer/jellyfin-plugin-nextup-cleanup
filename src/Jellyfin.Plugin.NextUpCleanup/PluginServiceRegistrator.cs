using Jellyfin.Plugin.NextUpCleanup.Filtering;
using Jellyfin.Plugin.NextUpCleanup.Tasks;
using Jellyfin.Plugin.NextUpCleanup.Web;
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
        // script is added through the File Transformation plugin — the same mechanism the
        // other home-screen plugins use. Registering it is all this does; if that plugin
        // is absent the toggle is simply not added and filtering is unaffected.
        serviceCollection.AddHostedService<ScriptRegistrationService>();

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
