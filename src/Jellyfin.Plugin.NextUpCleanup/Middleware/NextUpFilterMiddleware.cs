using System.Globalization;
using System.IO.Compression;
using System.Text;
using Jellyfin.Plugin.NextUpCleanup.Configuration;
using Jellyfin.Plugin.NextUpCleanup.Filtering;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.NextUpCleanup.Middleware;

/// <summary>
/// What kind of row an endpoint serves.
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
/// Intercepts Next Up responses and strips the first-episode entries that Jellyfin
/// 10.11 surfaces for series you never started — a side effect of jellyfin/jellyfin#13687,
/// which has no server-side setting to turn off. Nothing is written to the database:
/// disabling the plugin restores the stock behaviour immediately.
/// </summary>
internal sealed class NextUpFilterMiddleware
{
    private const string LimitKey = "limit";
    private const string StartIndexKey = "startIndex";

    private readonly RequestDelegate _next;
    private readonly ILogger<NextUpFilterMiddleware> _logger;

    public NextUpFilterMiddleware(RequestDelegate next, ILogger<NextUpFilterMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var config = Plugin.Instance?.Configuration;
        var kind = ClassifyEndpoint(context.Request.Path.Value);

        if (config is null || !config.Enabled || kind == EndpointKind.None)
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var mode = EffectiveMode(config, kind);

        var requestedLimit = OverfetchRequest(context, config);

        var originalBody = context.Response.Body;
        using var buffer = new MemoryStream();
        context.Response.Body = buffer;

        try
        {
            await _next(context).ConfigureAwait(false);

            context.Response.Body = originalBody;
            buffer.Seek(0, SeekOrigin.Begin);

            if (context.Response.StatusCode != StatusCodes.Status200OK || buffer.Length == 0)
            {
                await buffer.CopyToAsync(originalBody).ConfigureAwait(false);
                return;
            }

            var raw = buffer.ToArray();
            var encoding = context.Response.Headers.ContentEncoding.ToString();

            string json;
            try
            {
                json = await DecompressAsync(raw, encoding).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not decompress the Next Up response (encoding={Encoding}); passing it through", encoding);
                await originalBody.WriteAsync(raw).ConfigureAwait(false);
                return;
            }

            if (string.IsNullOrEmpty(json))
            {
                await originalBody.WriteAsync(raw).ConfigureAwait(false);
                return;
            }

            string filtered;
            int hidden;
            try
            {
                filtered = NextUpFilter.Apply(json, mode, requestedLimit, out hidden);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not filter the Next Up response for {Path}; passing it through", context.Request.Path);
                await originalBody.WriteAsync(raw).ConfigureAwait(false);
                return;
            }

            // Both halves are logged: "matched but hid nothing" is the answer to
            // "why is the row still full of S01E01", and silence means the row is
            // being served by an endpoint ClassifyEndpoint does not know about.
            _logger.LogDebug(
                "Next Up: hid {Hidden} first-episode entr(ies) on {Path} ({Kind}, mode {Mode})",
                hidden,
                context.Request.Path,
                kind,
                mode);

            var bytes = Encoding.UTF8.GetBytes(filtered);
            if (!string.IsNullOrEmpty(encoding))
            {
                bytes = await CompressAsync(bytes, encoding).ConfigureAwait(false);
            }

            context.Response.ContentLength = bytes.Length;
            await originalBody.WriteAsync(bytes).ConfigureAwait(false);
        }
        catch
        {
            context.Response.Body = originalBody;
            throw;
        }
    }

    /// <summary>
    /// Matches the endpoints that serve a Next Up row.
    /// <para>
    /// Matching is done on the trailing segments, so a base path left in front of the
    /// route by a reverse proxy (<c>/jellyfin/Shows/NextUp</c>) still matches.
    /// </para>
    /// </summary>
    internal static EndpointKind ClassifyEndpoint(string? path)
    {
        var parts = (path ?? string.Empty).Split('/', StringSplitOptions.RemoveEmptyEntries);

        // /Shows/NextUp — every stock client.
        if (EndsWith(parts, "Shows", "NextUp"))
        {
            return EndpointKind.NextUp;
        }

        // /HomeScreen/Section/{id} — the Home Screen Sections plugin and Jellyfin Enhanced
        // build their rows in process, so /Shows/NextUp never sees the request. The section
        // id is whatever the section was registered as, and a combined Continue Watching /
        // Next Up row is not called "NextUp", so match on what the id mentions. Sections
        // that are neither (Latest Media, My Media, Live TV) are left alone: a newly added
        // S01E01 belongs in those.
        if (parts.Length >= 3
            && Is(parts[^3], "HomeScreen")
            && Is(parts[^2], "Section"))
        {
            var section = parts[^1];

            if (Mentions(section, "Resume") || Mentions(section, "Continue"))
            {
                return EndpointKind.Mixed;
            }

            return Mentions(section, "NextUp") ? EndpointKind.NextUp : EndpointKind.None;
        }

        // /UserItems/Resume and the older /Users/{id}/Items/Resume — the Continue Watching
        // row. In 10.11 the web client merges Next Up into it, so the flood lands here too.
        if (EndsWith(parts, "UserItems", "Resume") || EndsWith(parts, "Items", "Resume"))
        {
            return EndpointKind.Mixed;
        }

        return EndpointKind.None;
    }

    private static bool EndsWith(string[] parts, string first, string second)
        => parts.Length >= 2 && Is(parts[^2], first) && Is(parts[^1], second);

    private static bool Is(string part, string value)
        => part.Equals(value, StringComparison.OrdinalIgnoreCase);

    private static bool Mentions(string part, string value)
        => part.Contains(value, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A row that is genuinely in progress must not lose an episode, so the blunt
    /// "hide every S01E01" mode is not applied to rows that carry resumable items.
    /// </summary>
    private static FilterMode EffectiveMode(PluginConfiguration config, EndpointKind kind)
        => kind == EndpointKind.Mixed ? FilterMode.UntouchedFirstEpisodes : config.Mode;

    /// <summary>
    /// Rewrites the inbound query so the server returns more rows than the client asked
    /// for. Without this, a client asking for 20 rows gets back however many survive
    /// filtering and the row visibly shrinks.
    /// </summary>
    /// <returns>
    /// The limit the client originally asked for, or null when nothing was rewritten
    /// (and so nothing needs trimming afterwards).
    /// </returns>
    private static int? OverfetchRequest(HttpContext context, PluginConfiguration config)
    {
        if (config.OverfetchMultiplier <= 1)
        {
            return null;
        }

        var query = context.Request.Query;

        // Paging past the first page cannot be over-fetched coherently: the server-side
        // offset would no longer line up with the filtered row being paged through.
        var startIndex = FindValue(query, StartIndexKey);
        if (!string.IsNullOrEmpty(startIndex) && int.TryParse(startIndex, out var start) && start > 0)
        {
            return null;
        }

        var limitValue = FindValue(query, LimitKey);
        if (string.IsNullOrEmpty(limitValue) || !int.TryParse(limitValue, out var limit) || limit <= 0)
        {
            return null;
        }

        var inflated = Math.Min((long)limit * config.OverfetchMultiplier, config.MaxOverfetchLimit);
        if (inflated <= limit)
        {
            return null;
        }

        var rewritten = new QueryBuilder();
        foreach (var pair in query)
        {
            if (pair.Key.Equals(LimitKey, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var value in pair.Value)
            {
                rewritten.Add(pair.Key, value ?? string.Empty);
            }
        }

        rewritten.Add(LimitKey, inflated.ToString(CultureInfo.InvariantCulture));
        context.Request.QueryString = rewritten.ToQueryString();

        return limit;
    }

    private static string? FindValue(IQueryCollection query, string key)
    {
        foreach (var pair in query)
        {
            if (pair.Key.Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                return pair.Value.ToString();
            }
        }

        return null;
    }

    private static async Task<string> DecompressAsync(byte[] data, string encoding)
    {
        if (string.IsNullOrEmpty(encoding))
        {
            return Encoding.UTF8.GetString(data);
        }

        using var input = new MemoryStream(data);
        Stream? decompressor = encoding.ToLowerInvariant() switch
        {
            "gzip" => new GZipStream(input, CompressionMode.Decompress),
            "br" => new BrotliStream(input, CompressionMode.Decompress),
            "deflate" => new DeflateStream(input, CompressionMode.Decompress),
            _ => null
        };

        if (decompressor is null)
        {
            // An encoding we don't know: don't guess, let the caller pass the bytes through.
            return string.Empty;
        }

        using (decompressor)
        using (var reader = new StreamReader(decompressor, Encoding.UTF8))
        {
            return await reader.ReadToEndAsync().ConfigureAwait(false);
        }
    }

    private static async Task<byte[]> CompressAsync(byte[] data, string encoding)
    {
        using var output = new MemoryStream();
        Stream? compressor = encoding.ToLowerInvariant() switch
        {
            "gzip" => new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true),
            "br" => new BrotliStream(output, CompressionLevel.Fastest, leaveOpen: true),
            "deflate" => new DeflateStream(output, CompressionLevel.Fastest, leaveOpen: true),
            _ => null
        };

        if (compressor is null)
        {
            return data;
        }

        await using (compressor.ConfigureAwait(false))
        {
            await compressor.WriteAsync(data).ConfigureAwait(false);
        }

        return output.ToArray();
    }
}
