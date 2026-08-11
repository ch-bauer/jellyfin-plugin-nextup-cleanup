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

    /// <summary>
    /// How many minutes of an episode have to have been played before it counts as
    /// started. Below this, a resume position is a stray tap or a look at the first
    /// minute — not something worth carrying on with, and not a reason to keep the
    /// episode in a row. 0 makes any resume position at all count.
    /// </summary>
    public int StartedWatchingMinutes { get; set; } = 5;

    /// <summary>
    /// The mark used by the <c>Reset abandoned episodes</c> scheduled task: an episode
    /// whose resume position is under this many minutes has its play state cleared.
    /// Separate from <see cref="StartedWatchingMinutes"/>, and lower, because this one
    /// deletes data rather than hiding a row entry.
    /// </summary>
    public int ResetBelowMinutes { get; set; } = 2;

    /// <summary>
    /// Collapse a series that has several part-way-through episodes in the row
    /// down to its most recently played one. Jellyfin lists every episode with
    /// progress, so skipping around in one show can fill the row with it.
    /// </summary>
    public bool DeduplicateSeries { get; set; } = true;

    /// <summary>
    /// How many episodes of one series may stay in the row. 1 is the strict
    /// "where I am in this show" case.
    /// </summary>
    public int MaxEpisodesPerSeries { get; set; } = 1;

    /// <summary>
    /// Collapse repeated movies too. Off by default: this only matters when the
    /// same film is in the library more than once.
    /// </summary>
    public bool DeduplicateMovies { get; set; }
}
