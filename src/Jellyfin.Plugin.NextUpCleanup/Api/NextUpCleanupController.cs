using System.Net.Mime;
using System.Reflection;
using System.Security.Claims;
using Jellyfin.Plugin.NextUpCleanup.Filtering;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.NextUpCleanup.Api;

/// <summary>
/// Backs the per-series toggle on the series detail page, and serves the small script
/// that puts the toggle there.
/// </summary>
[ApiController]
[Route("NextUpCleanup")]
public class NextUpCleanupController : ControllerBase
{
    private const string ScriptResource = "Jellyfin.Plugin.NextUpCleanup.Web.seriesToggle.js";

    private readonly SeriesExclusionStore _store;

    /// <summary>
    /// Initializes a new instance of the <see cref="NextUpCleanupController"/> class.
    /// </summary>
    /// <param name="store">The exclusion store.</param>
    public NextUpCleanupController(SeriesExclusionStore store)
    {
        _store = store;
    }

    /// <summary>
    /// Serves the client script. Anonymous because the web client loads it from
    /// index.html, which is served before anyone has logged in.
    /// </summary>
    /// <returns>The script.</returns>
    [HttpGet("script")]
    [AllowAnonymous]
    [Produces("application/javascript")]
    public ActionResult GetScript()
    {
        using var stream = typeof(NextUpCleanupController).Assembly.GetManifestResourceStream(ScriptResource);
        if (stream is null)
        {
            return NotFound();
        }

        using var reader = new StreamReader(stream);
        return Content(reader.ReadToEnd(), "application/javascript", System.Text.Encoding.UTF8);
    }

    /// <summary>
    /// The series the calling user has switched off.
    /// </summary>
    /// <returns>The excluded series ids, as strings.</returns>
    [HttpGet("Excluded")]
    [Authorize]
    public ActionResult<IEnumerable<string>> GetExcluded()
    {
        var userId = CurrentUserId();
        if (userId is null)
        {
            return Unauthorized();
        }

        return Ok(_store.ForUser(userId.Value).Select(e => e.SeriesId.ToString("N")));
    }

    /// <summary>
    /// Switches a series off for the calling user.
    /// </summary>
    /// <param name="seriesId">The series.</param>
    /// <param name="name">The series name, for a readable list on the dashboard page.</param>
    /// <returns>No content.</returns>
    [HttpPost("Excluded/{seriesId}")]
    [Authorize]
    public ActionResult Exclude([FromRoute] Guid seriesId, [FromQuery] string? name)
    {
        var userId = CurrentUserId();
        if (userId is null)
        {
            return Unauthorized();
        }

        _store.Add(userId.Value, seriesId, name);
        return NoContent();
    }

    /// <summary>
    /// Switches a series back on for the calling user.
    /// </summary>
    /// <param name="seriesId">The series.</param>
    /// <returns>No content.</returns>
    [HttpDelete("Excluded/{seriesId}")]
    [Authorize]
    public ActionResult Include([FromRoute] Guid seriesId)
    {
        var userId = CurrentUserId();
        if (userId is null)
        {
            return Unauthorized();
        }

        _store.Remove(userId.Value, seriesId);
        return NoContent();
    }

    /// <summary>
    /// The calling user, from the token Jellyfin authenticated the request with.
    /// </summary>
    private Guid? CurrentUserId()
    {
        var value = User.Claims
            .FirstOrDefault(c => c.Type.Equals("Jellyfin-UserId", StringComparison.OrdinalIgnoreCase))?.Value;

        return Guid.TryParse(value, out var userId) ? userId : null;
    }
}
