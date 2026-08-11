namespace Jellyfin.Plugin.NextUpCleanup.Web;

/// <summary>
/// What the File Transformation plugin hands a callback: the file as it stands so far,
/// after any earlier plugin has had its turn.
/// </summary>
public class PatchRequestPayload
{
    /// <summary>
    /// Gets or sets the current contents of the file being served.
    /// </summary>
    public string? Contents { get; set; }
}

/// <summary>
/// Adds the series-toggle script to jellyfin-web's index.html.
/// <para>
/// Called by the File Transformation plugin, which is the mechanism the other
/// home-screen plugins use and the only one that works reliably: index.html is a static
/// file with no server-side hook, writing into the web folder needs it writable and is
/// wiped on every jellyfin-web update, and middleware in front of the static-file handler
/// is not something a plugin can add on this server without breaking the pipeline.
/// </para>
/// </summary>
public static class TransformationPatches
{
    /// <summary>
    /// Appends the plugin's script tag to the document.
    /// </summary>
    /// <param name="content">The file as it stands.</param>
    /// <returns>The file with the script tag in it.</returns>
    public static string IndexHtml(PatchRequestPayload content)
    {
        var html = content.Contents ?? string.Empty;

        if (Plugin.Instance?.Configuration.ShowSeriesToggle != true)
        {
            return html;
        }

        // Another plugin may have transformed the document before this ran, and the
        // transformation itself can be registered more than once across a restart.
        if (html.Contains("/NextUpCleanup/script", StringComparison.OrdinalIgnoreCase))
        {
            return html;
        }

        var bodyClose = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
        if (bodyClose < 0)
        {
            return html;
        }

        // Relative to /web/, so a base path in front of the server resolves along with it.
        var version = typeof(Plugin).Assembly.GetName().Version?.ToString() ?? "1";
        var tag = $"<script plugin=\"NextUpCleanup\" version=\"{version}\" src=\"../NextUpCleanup/script?v={version}\" defer></script>";

        return html[..bodyClose] + tag + "\n" + html[bodyClose..];
    }
}
