using Jellyfin.Data.Enums;
using Jellyfin.Plugin.NextUpCleanup.Configuration;
using MediaBrowser.Model.Dto;

namespace Jellyfin.Plugin.NextUpCleanup.Filtering;

/// <summary>
/// What kind of row an action serves.
/// </summary>
internal enum EndpointKind
{
    /// <summary>Not a row this plugin touches.</summary>
    None = 0,

    /// <summary>A pure Next Up row.</summary>
    NextUp = 1,

    /// <summary>
    /// A row that mixes Next Up with Continue Watching, so it legitimately contains
    /// episodes the user is part-way through.
    /// </summary>
    Mixed = 2
}

/// <summary>
/// The pure part of the plugin: which actions serve a Next Up row, and which items in
/// one are the first-episode entries to drop. No HTTP and no server state, so it can be
/// tested on its own.
/// </summary>
internal static class NextUpFilter
{
    /// <summary>
    /// Decides what a request is from its MVC route, rather than from the URL text.
    /// <para>
    /// The controller and action are what Jellyfin actually dispatched to, so this is
    /// unaffected by a reverse-proxy base path, by the <c>/emby</c> prefix older clients
    /// use, or by the legacy spelling of a route — <c>/Users/{id}/Items/Resume</c> and
    /// <c>/UserItems/Resume</c> are two routes onto one controller, and both are caught
    /// here by their action names.
    /// </para>
    /// </summary>
    /// <param name="controller">The MVC controller name, without the "Controller" suffix.</param>
    /// <param name="action">The MVC action name.</param>
    /// <param name="sectionType">
    /// The <c>sectionType</c> route value, for the Home Screen Sections plugin's single
    /// endpoint that serves every one of its rows.
    /// </param>
    /// <returns>What kind of row the action serves.</returns>
    public static EndpointKind Classify(string? controller, string? action, string? sectionType)
    {
        if (string.IsNullOrEmpty(controller) || string.IsNullOrEmpty(action))
        {
            return EndpointKind.None;
        }

        // Stock Jellyfin: /Shows/NextUp, on every client.
        if (Is(controller, "TvShows") && Is(action, "GetNextUp"))
        {
            return EndpointKind.NextUp;
        }

        // Stock Jellyfin: the Continue Watching row. In 10.11 the web client merges Next Up
        // into it, so the flood lands here too. Two routes, two actions, one controller.
        if (Is(controller, "Items") && (Is(action, "GetResumeItems") || Is(action, "GetResumeItemsLegacy")))
        {
            return EndpointKind.Mixed;
        }

        // The Home Screen Sections plugin builds its rows in process and serves all of them
        // from one action, so /Shows/NextUp never sees the request. Which row it is is the
        // section id. Sections that are neither Next Up nor Continue Watching (Latest Media,
        // My Media, Live TV) are left alone: a newly added S01E01 belongs in those.
        if (Is(controller, "HomeScreen") && Is(action, "GetSectionContent"))
        {
            if (string.IsNullOrEmpty(sectionType))
            {
                return EndpointKind.None;
            }

            if (Mentions(sectionType, "Resume") || Mentions(sectionType, "Continue"))
            {
                return EndpointKind.Mixed;
            }

            return Mentions(sectionType, "NextUp") ? EndpointKind.NextUp : EndpointKind.None;
        }

        return EndpointKind.None;
    }

    /// <summary>
    /// True when this entry is the first episode of a first season — the thing Jellyfin
    /// 10.11 surfaces for series you never started. <c>S02E01</c> is left alone: that one
    /// means you finished season one.
    /// </summary>
    /// <param name="item">The entry.</param>
    /// <param name="mode">The configured mode.</param>
    /// <param name="kind">
    /// What kind of row this is. A row that also carries Continue Watching entries gets
    /// the resume-position guarantee below.
    /// </param>
    public static bool ShouldHide(BaseItemDto item, PluginConfiguration config, EndpointKind kind)
    {
        if (item.Type != BaseItemKind.Episode)
        {
            return false;
        }

        if (item.ParentIndexNumber != 1 || item.IndexNumber != 1)
        {
            return false;
        }

        // An episode you are genuinely part-way through is never taken out of a row that
        // carries Continue Watching, whichever mode is configured.
        if (kind == EndpointKind.Mixed && HasResumePosition(item, config))
        {
            return false;
        }

        return config.Mode switch
        {
            FilterMode.UntouchedFirstEpisodes => !HasStartedWatching(item, config),
            _ => true
        };
    }

    /// <summary>
    /// True when there is a resume position far enough in to be worth carrying on from.
    /// <para>
    /// Jellyfin stamps a resume position, a play count and a play date the moment playback
    /// starts, so without a floor under it a mis-tap, an auto-play that was stopped, or a
    /// look at the opening titles pins an episode to the row forever. A position under the
    /// threshold is treated as never having started — there is nothing there worth
    /// continuing.
    /// </para>
    /// </summary>
    private static bool HasResumePosition(BaseItemDto item, PluginConfiguration config)
    {
        var ticks = item.UserData?.PlaybackPositionTicks ?? 0;

        return ticks > 0 && ticks >= StartedTicks(config);
    }

    /// <summary>
    /// The playback position at which an episode counts as started.
    /// </summary>
    public static long StartedTicks(PluginConfiguration config)
        => Math.Max(0, config.StartedWatchingMinutes) * TimeSpan.TicksPerMinute;

    /// <summary>
    /// Collapses a show that appears several times in the row down to the episode you
    /// are furthest along in.
    /// <para>
    /// Jellyfin lists every episode that has progress on it, so skipping around in one
    /// series — or pausing a few episodes of it — fills the row with that one show. Which
    /// episode wins is decided by <c>LastPlayedDate</c> rather than by how far into the
    /// episode you are, so it is the one you actually watched most recently. An entry with
    /// no play date at all ranks last, since there is nothing to say it is the current one.
    /// </para>
    /// <para>
    /// The row's own order is kept: entries are only dropped, never reordered.
    /// </para>
    /// </summary>
    /// <param name="items">The row, in the order the server produced it.</param>
    /// <param name="config">The plugin configuration.</param>
    /// <returns>The row with the surplus entries of each show removed.</returns>
    public static List<BaseItemDto> Deduplicate(IReadOnlyList<BaseItemDto> items, PluginConfiguration config)
    {
        var maxPerSeries = Math.Max(1, config.MaxEpisodesPerSeries);

        // Index by group so the decision is made per show, then applied back in row order.
        var groups = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        var keys = new string?[items.Count];

        for (var i = 0; i < items.Count; i++)
        {
            var key = GroupKey(items[i], config);
            keys[i] = key;

            if (key is null)
            {
                continue;
            }

            if (!groups.TryGetValue(key, out var members))
            {
                members = new List<int>();
                groups[key] = members;
            }

            members.Add(i);
        }

        var keep = new HashSet<int>();
        foreach (var (key, members) in groups)
        {
            var allowed = key.StartsWith(MovieKeyPrefix, StringComparison.Ordinal) ? 1 : maxPerSeries;

            if (members.Count <= allowed)
            {
                keep.UnionWith(members);
                continue;
            }

            foreach (var index in members
                .OrderByDescending(i => items[i].UserData?.LastPlayedDate ?? DateTime.MinValue)
                .Take(allowed))
            {
                keep.Add(index);
            }
        }

        var result = new List<BaseItemDto>(items.Count);
        for (var i = 0; i < items.Count; i++)
        {
            // An entry that belongs to no group (a one-off, or a type not being
            // deduplicated) is never a duplicate of anything.
            if (keys[i] is null || keep.Contains(i))
            {
                result.Add(items[i]);
            }
        }

        return result;
    }

    private const string MovieKeyPrefix = "movie:";

    /// <summary>
    /// What makes two entries the same thing. Episodes are the same show when they share
    /// a series; movies are the same film when they share a name, which is the only case
    /// a movie can legitimately repeat in the row.
    /// </summary>
    private static string? GroupKey(BaseItemDto item, PluginConfiguration config)
    {
        if (item.Type == BaseItemKind.Episode)
        {
            if (!config.DeduplicateSeries)
            {
                return null;
            }

            if (item.SeriesId is Guid seriesId && seriesId != Guid.Empty)
            {
                return "series:" + seriesId.ToString("N");
            }

            return string.IsNullOrEmpty(item.SeriesName) ? null : "series:" + item.SeriesName;
        }

        if (item.Type == BaseItemKind.Movie && config.DeduplicateMovies)
        {
            return string.IsNullOrEmpty(item.Name) ? null : MovieKeyPrefix + item.Name;
        }

        return null;
    }

    /// <summary>
    /// True if the user has actually watched this episode, as opposed to having merely
    /// touched it.
    /// <para>
    /// Only two things count: it is marked played, or it has a resume position past the
    /// threshold. A play count and a play date deliberately do not, because Jellyfin
    /// writes both the instant playback starts — so treating them as evidence made every
    /// episode anybody ever pressed play on look like one you are in the middle of, which
    /// is the whole reason a first episode could not be got rid of.
    /// </para>
    /// </summary>
    public static bool HasStartedWatching(BaseItemDto item, PluginConfiguration config)
    {
        var userData = item.UserData;
        if (userData is null)
        {
            return false;
        }

        return userData.Played || HasResumePosition(item, config);
    }

    private static bool Is(string value, string expected)
        => value.Equals(expected, StringComparison.OrdinalIgnoreCase);

    private static bool Mentions(string value, string expected)
        => value.Contains(expected, StringComparison.OrdinalIgnoreCase);
}
