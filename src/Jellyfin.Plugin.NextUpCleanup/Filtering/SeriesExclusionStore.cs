using Jellyfin.Plugin.NextUpCleanup.Configuration;

namespace Jellyfin.Plugin.NextUpCleanup.Filtering;

/// <summary>
/// The set of series each user has switched off from a series detail page.
/// <para>
/// Reads are on the hot path — every row entry is checked — so the set is held in memory
/// and the plugin configuration is written through on change, rather than being re-read
/// per request.
/// </para>
/// </summary>
public sealed class SeriesExclusionStore
{
    private readonly object _gate = new();
    private HashSet<(Guid User, Guid Series)>? _excluded;

    // The configuration object the cache was built from. Saving from the dashboard hands
    // back a whole new object rather than mutating this one, so comparing the reference is
    // what tells the cache it is looking at stale data. Checking it beats subscribing to a
    // change event: there is nothing to wire up, nothing to unsubscribe, and no window
    // between the plugin being constructed and the subscription being made.
    private PluginConfiguration? _builtFrom;

    /// <summary>
    /// True if this user has switched this series off. False for an empty series id, so
    /// an entry Jellyfin gave no series for is never mistaken for an excluded one.
    /// </summary>
    public bool IsExcluded(Guid userId, Guid seriesId)
    {
        if (userId == Guid.Empty || seriesId == Guid.Empty)
        {
            return false;
        }

        lock (_gate)
        {
            return Snapshot().Contains((userId, seriesId));
        }
    }

    /// <summary>
    /// The series this user has switched off.
    /// </summary>
    public IReadOnlyList<ExcludedSeries> ForUser(Guid userId)
    {
        lock (_gate)
        {
            var config = Plugin.Instance?.Configuration;
            if (config is null)
            {
                return Array.Empty<ExcludedSeries>();
            }

            return config.ExcludedSeries.Where(e => e.UserId == userId).ToList();
        }
    }

    /// <summary>
    /// Switches a series off for a user.
    /// </summary>
    /// <returns>False if it was already off, so the caller can skip a needless save.</returns>
    public bool Add(Guid userId, Guid seriesId, string? name)
    {
        lock (_gate)
        {
            var config = Plugin.Instance?.Configuration;
            if (config is null || userId == Guid.Empty || seriesId == Guid.Empty)
            {
                return false;
            }

            if (config.ExcludedSeries.Any(e => e.UserId == userId && e.SeriesId == seriesId))
            {
                return false;
            }

            config.ExcludedSeries.Add(new ExcludedSeries { UserId = userId, SeriesId = seriesId, Name = name });
            Save();
            return true;
        }
    }

    /// <summary>
    /// Switches a series back on for a user.
    /// </summary>
    /// <returns>False if it was not off in the first place.</returns>
    public bool Remove(Guid userId, Guid seriesId)
    {
        lock (_gate)
        {
            var config = Plugin.Instance?.Configuration;
            if (config is null)
            {
                return false;
            }

            var existing = config.ExcludedSeries
                .Where(e => e.UserId == userId && e.SeriesId == seriesId)
                .ToList();

            if (existing.Count == 0)
            {
                return false;
            }

            foreach (var entry in existing)
            {
                config.ExcludedSeries.Remove(entry);
            }

            Save();
            return true;
        }
    }

    /// <summary>
    /// Drops the in-memory set, so the next read rebuilds it from configuration.
    /// </summary>
    public void Invalidate()
    {
        lock (_gate)
        {
            _excluded = null;
            _builtFrom = null;
        }
    }

    private void Save()
    {
        _excluded = null;
        _builtFrom = null;
        Plugin.Instance?.SaveConfiguration();
    }

    private HashSet<(Guid, Guid)> Snapshot()
    {
        var config = Plugin.Instance?.Configuration;

        if (_excluded is not null && ReferenceEquals(config, _builtFrom))
        {
            return _excluded;
        }

        _excluded = config is null
            ? new HashSet<(Guid, Guid)>()
            : config.ExcludedSeries.Select(e => (e.UserId, e.SeriesId)).ToHashSet();
        _builtFrom = config;

        return _excluded;
    }
}
