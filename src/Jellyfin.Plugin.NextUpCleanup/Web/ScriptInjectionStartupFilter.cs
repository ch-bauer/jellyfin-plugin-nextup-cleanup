using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.NextUpCleanup.Web;

/// <summary>
/// Puts the plugin's client script into jellyfin-web's index.html at request time.
/// <para>
/// index.html is a static file, so there is no MVC action to hook and no server-side hook
/// for adding a script to it. Middleware ahead of the static-file handler is the way in,
/// and it keeps the plugin from writing into the web folder — that needs a writable web
/// root and is wiped every time jellyfin-web is updated.
/// </para>
/// <para>
/// It is deliberately additive and unfailing: it only touches the web index, it no-ops if
/// the tag is already there, and on any error it serves the original response. A broken
/// injection must never cost anybody their web client.
/// </para>
/// </summary>
internal sealed class ScriptInjectionStartupFilter : IStartupFilter
{
    private readonly ILogger<ScriptInjectionStartupFilter> _logger;
    private int _loggedOnce;

    public ScriptInjectionStartupFilter(ILogger<ScriptInjectionStartupFilter> logger)
    {
        _logger = logger;
    }

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        return app =>
        {
            // Ahead of everything else, so stripping Accept-Encoding below reliably
            // yields an uncompressed body to rewrite.
            app.Use(InvokeAsync);
            next(app);
        };
    }

    private async Task InvokeAsync(HttpContext context, Func<Task> next)
    {
        var config = Plugin.Instance?.Configuration;

        if (config is null
            || !config.ShowSeriesToggle
            || !IsIndexRequest(context.Request.Path.Value)
            || !HttpMethods.IsGet(context.Request.Method))
        {
            await next().ConfigureAwait(false);
            return;
        }

        // Make the static handler produce a complete, plain 200 that can be rewritten:
        // no compression, and no partial 206 that would pass through un-injected.
        context.Request.Headers.Remove("Accept-Encoding");
        context.Request.Headers.Remove("Range");
        context.Request.Headers.Remove("If-Range");

        var originalBody = context.Response.Body;
        using var buffer = new MemoryStream();
        context.Response.Body = buffer;

        try
        {
            await next().ConfigureAwait(false);
        }
        catch
        {
            // Not ours to swallow. The real response has not started, so the host can
            // still render its error page — flushing the partial buffer here would
            // commit a truncated body that looks like a 200.
            context.Response.Body = originalBody;
            throw;
        }

        context.Response.Body = originalBody;
        buffer.Seek(0, SeekOrigin.Begin);

        var isHtml = context.Response.StatusCode == StatusCodes.Status200OK
            && (context.Response.ContentType?.Contains("text/html", StringComparison.OrdinalIgnoreCase) ?? false);

        if (!isHtml)
        {
            await buffer.CopyToAsync(originalBody).ConfigureAwait(false);
            return;
        }

        string html;
        using (var reader = new StreamReader(buffer, Encoding.UTF8, true, 1024, leaveOpen: true))
        {
            html = await reader.ReadToEndAsync().ConfigureAwait(false);
        }

        try
        {
            var alreadyInjected = html.Contains("/NextUpCleanup/script", StringComparison.OrdinalIgnoreCase);
            var bodyClose = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);

            if (!alreadyInjected && bodyClose >= 0)
            {
                // Relative to /web/, so a base path in front of the server resolves with it.
                var version = typeof(Plugin).Assembly.GetName().Version?.ToString() ?? "1";
                var tag = $"<script plugin=\"NextUpCleanup\" src=\"../NextUpCleanup/script?v={version}\" defer></script>";

                html = html[..bodyClose] + tag + "\n" + html[bodyClose..];

                if (Interlocked.Exchange(ref _loggedOnce, 1) == 0)
                {
                    _logger.LogInformation("Next Up Cleanup: injected the series-toggle script into the web client");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Next Up Cleanup: could not inject the series-toggle script; serving the page unchanged");
        }

        var bytes = Encoding.UTF8.GetBytes(html);
        context.Response.ContentType = "text/html;charset=utf-8";
        context.Response.ContentLength = bytes.Length;

        // The body changed, so the static handler's validators no longer describe it.
        context.Response.Headers.Remove("ETag");
        context.Response.Headers.Remove("Last-Modified");
        context.Response.Headers.Remove("Accept-Ranges");

        await originalBody.WriteAsync(bytes).ConfigureAwait(false);
    }

    /// <summary>
    /// The web app shell, however it is asked for: "/web", "/web/", "/web/index.html".
    /// Matching the end keeps it correct behind a base-url prefix.
    /// </summary>
    private static bool IsIndexRequest(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        return path.EndsWith("/web/index.html", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith("/web/", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/web", StringComparison.OrdinalIgnoreCase);
    }
}
