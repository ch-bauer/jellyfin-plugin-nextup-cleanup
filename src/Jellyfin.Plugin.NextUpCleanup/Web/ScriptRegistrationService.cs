using System.Reflection;
using System.Runtime.Loader;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.NextUpCleanup.Web;

/// <summary>
/// Asks the File Transformation plugin to run <see cref="TransformationPatches.IndexHtml"/>
/// over jellyfin-web's index.html, which is how the series-toggle script gets onto the page.
/// <para>
/// The call is made by reflection on purpose: Jellyfin loads each plugin into its own
/// assembly load context, so File Transformation cannot be referenced as a library — this
/// is the integration its author documents. Nothing here is required for filtering; if the
/// plugin is not installed, the toggle simply does not appear and everything else works.
/// </para>
/// </summary>
public sealed class ScriptRegistrationService : IHostedService
{
    // Stable, so re-registering across restarts replaces rather than stacks.
    private const string TransformationId = "9a0f4e0c-6b1a-4a51-9f4c-3d1c2b7a5e10";

    private readonly ILogger<ScriptRegistrationService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ScriptRegistrationService"/> class.
    /// </summary>
    /// <param name="logger">The logger.</param>
    public ScriptRegistrationService(ILogger<ScriptRegistrationService> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            Register();
        }
        catch (Exception ex)
        {
            // The toggle is a convenience; filtering is the plugin. Never take the server
            // down over a missing or changed third-party plugin.
            _logger.LogError(ex, "Next Up Cleanup: could not register the series-toggle script; the toggle will not appear");
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private void Register()
    {
        var assembly = AssemblyLoadContext.All
            .SelectMany(context => context.Assemblies)
            .FirstOrDefault(a => a.FullName?.Contains(".FileTransformation", StringComparison.Ordinal) == true);

        if (assembly is null)
        {
            _logger.LogInformation(
                "Next Up Cleanup: the File Transformation plugin is not installed, so the series toggle cannot be added to the web client. Everything else works without it");
            return;
        }

        var pluginInterface = assembly.GetType("Jellyfin.Plugin.FileTransformation.PluginInterface");
        var register = pluginInterface?.GetMethod("RegisterTransformation", BindingFlags.Public | BindingFlags.Static);

        if (register is null)
        {
            _logger.LogWarning(
                "Next Up Cleanup: the installed File Transformation plugin has no RegisterTransformation(payload); the series toggle will not appear");
            return;
        }

        // The payload is passed as the type that method expects — a JObject in every
        // released version — so it is built through the parameter's own type rather than
        // taking a Newtonsoft dependency this plugin does not otherwise need.
        var payloadType = register.GetParameters().FirstOrDefault()?.ParameterType;
        if (payloadType is null || Activator.CreateInstance(payloadType) is not object payload)
        {
            _logger.LogWarning("Next Up Cleanup: could not build a transformation payload; the series toggle will not appear");
            return;
        }

        var add = payloadType.GetMethod("Add", new[] { typeof(string), payloadType.Assembly.GetType("Newtonsoft.Json.Linq.JToken") ?? typeof(object) })
            ?? payloadType.GetMethods().FirstOrDefault(m => m.Name == "Add" && m.GetParameters().Length == 2);

        if (add is null)
        {
            _logger.LogWarning("Next Up Cleanup: transformation payload of type {Type} takes no Add(name, value); the series toggle will not appear", payloadType.Name);
            return;
        }

        void Set(string name, string value)
        {
            var parameterType = add.GetParameters()[1].ParameterType;
            object converted = parameterType.IsAssignableFrom(typeof(string))
                ? value
                : Convert(parameterType, value);

            add.Invoke(payload, new[] { name, converted });
        }

        Set("id", TransformationId);
        Set("fileNamePattern", "index.html");
        Set("callbackAssembly", typeof(ScriptRegistrationService).Assembly.FullName ?? string.Empty);
        Set("callbackClass", typeof(TransformationPatches).FullName ?? string.Empty);
        Set("callbackMethod", nameof(TransformationPatches.IndexHtml));

        register.Invoke(null, new[] { payload });

        _logger.LogInformation("Next Up Cleanup: registered the series-toggle script with the File Transformation plugin");
    }

    /// <summary>
    /// Wraps a string in whatever token type the payload wants — JValue in practice.
    /// </summary>
    private static object Convert(Type parameterType, string value)
    {
        var jvalue = parameterType.Assembly.GetType("Newtonsoft.Json.Linq.JValue");
        if (jvalue is not null)
        {
            var instance = Activator.CreateInstance(jvalue, value);
            if (instance is not null)
            {
                return instance;
            }
        }

        return value;
    }
}
