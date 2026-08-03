using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.NextUpCleanup.Configuration;

/// <summary>
/// Which first episodes get hidden from Next Up.
/// </summary>
public enum FilterMode
{
    /// <summary>
    /// Hide every S01E01, unconditionally. Blunt but predictable — this is the
    /// behaviour Jellyfin had before PR #13687 for series with no play history,
    /// and it also hides a S01E01 you happen to be part-way through.
    /// </summary>
    AllFirstEpisodes = 0,

    /// <summary>
    /// Hide S01E01 only when that episode has no play state of its own —
    /// no resume position, no play count, never marked played. A first episode
    /// you paused half-way through stays in the row.
    /// </summary>
    UntouchedFirstEpisodes = 1
}

public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Master switch. When false the middleware is a pass-through and no
    /// response body is touched.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Which first episodes to hide.
    /// </summary>
    public FilterMode Mode { get; set; } = FilterMode.AllFirstEpisodes;

    /// <summary>
    /// Ask the server for more rows than the client requested so that the row
    /// is still full after filtering. Set to 1 to disable over-fetching.
    /// </summary>
    public int OverfetchMultiplier { get; set; } = 3;

    /// <summary>
    /// Hard ceiling on the over-fetched limit, so a client asking for 100 rows
    /// cannot turn into a 300-row query.
    /// </summary>
    public int MaxOverfetchLimit { get; set; } = 150;
}
