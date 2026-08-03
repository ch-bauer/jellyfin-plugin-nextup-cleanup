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

        if (config is null || !config.Enabled || !IsNextUpEndpoint(context.Request.Path.Value))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

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
                filtered = NextUpFilter.Apply(json, config, requestedLimit, out hidden);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not filter the Next Up response for {Path}; passing it through", context.Request.Path);
                await originalBody.WriteAsync(raw).ConfigureAwait(false);
                return;
            }

            if (hidden > 0)
            {
                _logger.LogDebug("Next Up: hid {Hidden} first-episode entr(ies) on {Path}", hidden, context.Request.Path);
            }

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
    /// </summary>
    internal static bool IsNextUpEndpoint(string? path)
    {
        var parts = (path ?? string.Empty).Trim('/').Split('/');

        // /Shows/NextUp — every stock client.
        if (parts.Length == 2
            && parts[0].Equals("Shows", StringComparison.OrdinalIgnoreCase)
            && parts[1].Equals("NextUp", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // /HomeScreen/Section/NextUp — the Home Screen Sections plugin and Jellyfin
        // Enhanced serve their own home rows and never touch /Shows/NextUp.
        if (parts.Length == 3
            && parts[0].Equals("HomeScreen", StringComparison.OrdinalIgnoreCase)
            && parts[1].Equals("Section", StringComparison.OrdinalIgnoreCase)
            && parts[2].StartsWith("NextUp", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

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
