using System.Text.Json.Nodes;
using Jellyfin.Plugin.NextUpCleanup.Configuration;

namespace Jellyfin.Plugin.NextUpCleanup.Filtering;

/// <summary>
/// The pure part of the plugin: takes a Next Up response body and gives back one
/// without the first-episode entries. No HTTP, no server state — just JSON in,
/// JSON out, so it can be tested on its own.
/// </summary>
internal static class NextUpFilter
{
    /// <summary>
    /// Removes first-episode entries from a <c>QueryResult</c> body.
    /// </summary>
    /// <param name="json">The response body.</param>
    /// <param name="mode">
    /// Which first episodes to hide. This is the configured mode for a pure Next Up row,
    /// but the caller narrows it for rows that also carry Continue Watching entries.
    /// </param>
    /// <param name="requestedLimit">
    /// The limit the client originally asked for, when the request was over-fetched.
    /// The result is trimmed back to it. Null leaves the page length alone.
    /// </param>
    /// <param name="hidden">How many entries were removed.</param>
    /// <returns>The rewritten body, or <paramref name="json"/> unchanged if nothing was hidden.</returns>
    public static string Apply(string json, FilterMode mode, int? requestedLimit, out int hidden)
    {
        hidden = 0;

        var root = JsonNode.Parse(json);
        var items = root?["Items"]?.AsArray();
        if (root is null || items is null || items.Count == 0)
        {
            return json;
        }

        var kept = new JsonArray();
        var removed = 0;

        foreach (var item in items)
        {
            if (item is null)
            {
                continue;
            }

            if (ShouldHide(item, mode))
            {
                removed++;
                continue;
            }

            if (requestedLimit is int max && kept.Count >= max)
            {
                // Over-fetched surplus: not hidden, just past the client's page.
                break;
            }

            // Nodes carry a parent, so they have to be detached before re-adding.
            var clone = JsonNode.Parse(item.ToJsonString());
            if (clone is not null)
            {
                kept.Add(clone);
            }
        }

        if (removed == 0)
        {
            return json;
        }

        hidden = removed;

        // The stock count came from the unfiltered query, and we can only subtract
        // what we actually saw — so on an over-fetched page this is an estimate.
        // It never claims fewer rows than we are returning.
        if (root["TotalRecordCount"]?.GetValue<int>() is int total)
        {
            root["TotalRecordCount"] = Math.Max(kept.Count, total - removed);
        }

        root["Items"] = kept;

        return root.ToJsonString();
    }

    private static bool ShouldHide(JsonNode item, FilterMode mode)
    {
        if (!string.Equals(item["Type"]?.GetValue<string>(), "Episode", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (item["ParentIndexNumber"]?.GetValue<int>() != 1 || item["IndexNumber"]?.GetValue<int>() != 1)
        {
            return false;
        }

        return mode switch
        {
            FilterMode.UntouchedFirstEpisodes => !HasPlayState(item),
            _ => true
        };
    }

    /// <summary>
    /// True if the user has any relationship with this episode at all: a resume
    /// position, a play count, a played flag, or a recorded play date.
    /// </summary>
    private static bool HasPlayState(JsonNode item)
    {
        var userData = item["UserData"];
        if (userData is null)
        {
            return false;
        }

        if (userData["PlaybackPositionTicks"]?.GetValue<long>() > 0)
        {
            return true;
        }

        if (userData["PlayCount"]?.GetValue<int>() > 0)
        {
            return true;
        }

        if (userData["Played"]?.GetValue<bool>() == true)
        {
            return true;
        }

        return !string.IsNullOrEmpty(userData["LastPlayedDate"]?.GetValue<string>());
    }
}
