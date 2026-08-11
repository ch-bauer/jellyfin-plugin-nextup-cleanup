using System.Collections.Concurrent;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.NextUpCleanup.Configuration;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.NextUpCleanup.Filtering;

/// <summary>
/// Strips the first-episode entries that Jellyfin 10.11 surfaces for series you never
/// started — a side effect of jellyfin/jellyfin#13687, which has no server-side setting
/// to turn off. Nothing is written to the database: disabling the plugin restores the
/// stock behaviour immediately.
/// <para>
/// This is an MVC action filter rather than response middleware, which is what makes it
/// work on every client. It edits the <see cref="QueryResult{BaseItemDto}"/> the
/// controller returned, before anything is serialised — so it is unaffected by which
/// JSON casing profile the client negotiated, by response compression, and by whatever
/// other plugins do to the body afterwards. It also reaches rows served by plugin
/// controllers, such as the Home Screen Sections plugin, whose sections never touch
/// <c>/Shows/NextUp</c>.
/// </para>
/// </summary>
internal sealed class NextUpActionFilter : IAsyncActionFilter
{
    private const string LimitKey = "limit";
    private const string StartIndexKey = "startIndex";
    private const string SectionTypeKey = "sectionType";
    private const string UserIdKey = "userId";

    private static readonly TimeSpan WarnInterval = TimeSpan.FromHours(1);
    private static readonly ConcurrentDictionary<string, DateTime> _warnedAt = new(StringComparer.OrdinalIgnoreCase);

    private readonly ILogger<NextUpActionFilter> _logger;
    private readonly SeriesExclusionStore _exclusions;

    public NextUpActionFilter(ILogger<NextUpActionFilter> logger, SeriesExclusionStore exclusions)
    {
        _logger = logger;
        _exclusions = exclusions;
    }

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var config = Plugin.Instance?.Configuration;
        var route = context.RouteData.Values;

        var kind = NextUpFilter.Classify(
            route.TryGetValue("controller", out var controller) ? controller as string : null,
            route.TryGetValue("action", out var action) ? action as string : null,
            SectionType(context));

        if (config is null || !config.Enabled || kind == EndpointKind.None)
        {
            var executedElsewhere = await next().ConfigureAwait(false);
            LogNearMiss(executedElsewhere, controller as string, action as string);
            return;
        }

        var requestedLimit = Overfetch(context, config);

        var executed = await next().ConfigureAwait(false);

        try
        {
            Rewrite(executed, config, requestedLimit, kind, context, CurrentUserId(context));
        }
        catch (Exception ex)
        {
            // A broken filter degrades to stock Jellyfin, never to a broken row.
            _logger.LogError(ex, "Could not filter the Next Up row for {Controller}/{Action}; leaving it untouched", controller, action);
        }
    }

    private void Rewrite(
        ActionExecutedContext executed,
        PluginConfiguration config,
        int? requestedLimit,
        EndpointKind kind,
        ActionExecutingContext context,
        Guid userId)
    {
        if (executed.Result is not ObjectResult result || result.Value is not QueryResult<BaseItemDto> query)
        {
            // An action that threw has no result to filter, and the server is already
            // logging the reason.
            if (executed.Exception is null)
            {
                WarnAboutShapeOnce(context, executed);
            }

            return;
        }

        var items = query.Items;
        if (items is null || items.Count == 0)
        {
            return;
        }

        var kept = new List<BaseItemDto>(items.Count);
        var excluded = 0;

        foreach (var item in items)
        {
            // A series switched off from its detail page leaves outright: no episode
            // number, play state or threshold gets a say.
            if (item.SeriesId is Guid seriesId && _exclusions.IsExcluded(userId, seriesId))
            {
                excluded++;
                continue;
            }

            if (!NextUpFilter.ShouldHide(item, config, kind))
            {
                kept.Add(item);
            }
        }

        var hidden = items.Count - kept.Count - excluded;

        // Deduplicating after hiding, so a show whose only surviving entry is its S01E01
        // is not represented by an entry that is about to be removed anyway.
        var deduplicated = NextUpFilter.Deduplicate(kept, config);
        var duplicates = kept.Count - deduplicated.Count;

        // Over-fetched surplus: not removed, just past the page the client asked for. It
        // still has to come off, or a client that asked for 20 rows renders 60.
        var trimmed = 0;
        if (requestedLimit is int max && deduplicated.Count > max)
        {
            trimmed = deduplicated.Count - max;
            deduplicated.RemoveRange(max, trimmed);
        }

        // All of it is logged: "matched but removed nothing" is the answer to "why is the
        // row still full of S01E01", and silence means the row is being served by an
        // action Classify does not know about.
        _logger.LogDebug(
            "Next Up: {Controller}/{Action} ({Kind}, mode {Mode}) — {Total} entr(ies) in, dropped {Excluded} from switched-off series, hid {Hidden} first episode(s), collapsed {Duplicates} duplicate(s), trimmed {Trimmed} over-fetched, {Kept} out",
            context.RouteData.Values["controller"],
            context.RouteData.Values["action"],
            kind,
            config.Mode,
            items.Count,
            excluded,
            hidden,
            duplicates,
            trimmed,
            deduplicated.Count);

        if (hidden == 0 && duplicates == 0 && trimmed == 0 && excluded == 0)
        {
            return;
        }

        // The stock count came from the unfiltered query, and we can only subtract what we
        // actually saw — so on an over-fetched page this is an estimate. It never claims
        // fewer rows than we are returning.
        result.Value = new QueryResult<BaseItemDto>(
            query.StartIndex,
            Math.Max(deduplicated.Count, query.TotalRecordCount - hidden - duplicates - excluded),
            deduplicated);
    }

    /// <summary>
    /// Asks the controller for more rows than the client wanted, so the row is still full
    /// after filtering. This edits the bound action argument rather than the query string,
    /// so it applies whichever route the client came in on.
    /// </summary>
    /// <returns>
    /// The limit the client originally asked for, or null when nothing was inflated
    /// (and so nothing needs trimming afterwards).
    /// </returns>
    private static int? Overfetch(ActionExecutingContext context, PluginConfiguration config)
    {
        if (config.OverfetchMultiplier <= 1)
        {
            return null;
        }

        // Paging past the first page cannot be over-fetched coherently: the offset would
        // no longer line up with the filtered row being paged through.
        if (context.ActionArguments.TryGetValue(StartIndexKey, out var rawStart)
            && rawStart is int start
            && start > 0)
        {
            return null;
        }

        // An action with no limit parameter (the Home Screen Sections endpoint fixes its
        // own row length) has nothing to inflate.
        if (!context.ActionArguments.TryGetValue(LimitKey, out var rawLimit)
            || rawLimit is not int limit
            || limit <= 0)
        {
            return null;
        }

        var inflated = (int)Math.Min((long)limit * config.OverfetchMultiplier, config.MaxOverfetchLimit);
        if (inflated <= limit)
        {
            return null;
        }

        context.ActionArguments[LimitKey] = inflated;
        return limit;
    }

    /// <summary>
    /// Reports a row that came back in a shape this filter cannot edit — most likely a
    /// Jellyfin upgrade changed the return type — rather than silently doing nothing.
    /// Once an hour per action, so a permanent mismatch does not fill the log with a line
    /// per request while still staying visible after the first one scrolls away.
    /// </summary>
    private void WarnAboutShapeOnce(ActionExecutingContext context, ActionExecutedContext executed)
    {
        var controller = context.RouteData.Values["controller"] as string;
        var action = context.RouteData.Values["action"] as string;
        var key = controller + "/" + action;
        var now = DateTime.UtcNow;

        // AddOrUpdate hands back the stored timestamp; getting our own back means this
        // call won the slot and is the one that logs.
        var stored = _warnedAt.AddOrUpdate(
            key,
            now,
            (_, last) => now - last >= WarnInterval ? now : last);

        if (stored != now)
        {
            return;
        }

        _logger.LogWarning(
            "Next Up: {Route} returned {Type}, not a QueryResult<BaseItemDto>; that row is not being filtered. Re-warns hourly",
            key,
            executed.Result?.GetType().Name ?? "nothing");
    }

    /// <summary>
    /// Whose row this is. Exclusions are per user, so the row has to be filtered against
    /// the user it was built for.
    /// <para>
    /// That is the <c>userId</c> the endpoint was called with, when it takes one — an
    /// administrator or an API key can ask for somebody else's row, and it is that
    /// person's list of switched-off series that belongs on it, not the caller's. Only
    /// when the endpoint carries no user does this fall back to the token the request was
    /// authenticated with.
    /// </para>
    /// </summary>
    private static Guid CurrentUserId(ActionExecutingContext context)
    {
        if (context.ActionArguments.TryGetValue(UserIdKey, out var argument))
        {
            if (argument is Guid fromArgument && fromArgument != Guid.Empty)
            {
                return fromArgument;
            }

            if (argument is string text && Guid.TryParse(text, out var parsed))
            {
                return parsed;
            }
        }

        foreach (var pair in context.HttpContext.Request.Query)
        {
            if (pair.Key.Equals(UserIdKey, StringComparison.OrdinalIgnoreCase)
                && Guid.TryParse(pair.Value.ToString(), out var fromQuery))
            {
                return fromQuery;
            }
        }

        var claim = context.HttpContext.User.Claims
            .FirstOrDefault(c => c.Type.Equals("Jellyfin-UserId", StringComparison.OrdinalIgnoreCase))?.Value;

        return Guid.TryParse(claim, out var userId) ? userId : Guid.Empty;
    }

    private static string? SectionType(ActionExecutingContext context)
        => context.RouteData.Values.TryGetValue(SectionTypeKey, out var value) ? value as string : null;

    /// <summary>
    /// Reports a row this plugin is not filtering but probably should be: an action that
    /// returned first episodes of a first season. Without this, an unrecognised endpoint
    /// is indistinguishable from a filter that matched and found nothing — both log
    /// nothing at all, and the row stays full either way. Debug-level only, and the
    /// result is not inspected unless debug logging is actually on.
    /// </summary>
    private void LogNearMiss(ActionExecutedContext executed, string? controller, string? action)
    {
        if (!_logger.IsEnabled(LogLevel.Debug)
            || executed.Result is not ObjectResult result
            || result.Value is not QueryResult<BaseItemDto> query
            || query.Items is null)
        {
            return;
        }

        var firstEpisodes = query.Items.Count(
            item => item.Type == BaseItemKind.Episode && item.ParentIndexNumber == 1 && item.IndexNumber == 1);

        if (firstEpisodes > 0)
        {
            _logger.LogDebug(
                "Next Up: {Controller}/{Action} returned {Count} first-episode entr(ies) and is not an endpoint this plugin filters",
                controller,
                action,
                firstEpisodes);
        }
    }
}
