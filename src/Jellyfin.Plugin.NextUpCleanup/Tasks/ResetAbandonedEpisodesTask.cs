using System.Collections;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.NextUpCleanup.Tasks;

/// <summary>
/// Clears the play state of episodes that were barely started — the stray taps, the
/// auto-plays that were stopped, the looks at the opening titles. Jellyfin writes a resume
/// position, a play count and a play date on the first frame, and nothing ever takes them
/// off again, so they accumulate and keep episodes pinned to Continue Watching.
/// <para>
/// This is the one thing in the plugin that writes to the database, and it cannot be
/// undone, so it never runs on its own: it has no default trigger and only does anything
/// when started by hand from Dashboard → Scheduled Tasks. Filtering already keeps these
/// entries out of every row without it — this is for tidying what is behind them.
/// </para>
/// </summary>
public class ResetAbandonedEpisodesTask : IScheduledTask
{
    private readonly IUserManager _userManager;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserDataManager _userDataManager;
    private readonly ILogger<ResetAbandonedEpisodesTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ResetAbandonedEpisodesTask"/> class.
    /// </summary>
    /// <param name="userManager">The user manager.</param>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="userDataManager">The user data manager.</param>
    /// <param name="logger">The logger.</param>
    public ResetAbandonedEpisodesTask(
        IUserManager userManager,
        ILibraryManager libraryManager,
        IUserDataManager userDataManager,
        ILogger<ResetAbandonedEpisodesTask> logger)
    {
        _userManager = userManager;
        _libraryManager = libraryManager;
        _userDataManager = userDataManager;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Reset abandoned episodes";

    /// <inheritdoc />
    public string Key => "NextUpCleanupResetAbandoned";

    /// <inheritdoc />
    public string Description =>
        "Clears the play state of episodes with a resume position under the configured mark, "
        + "so a mis-tap or a stopped auto-play stops counting as something you are watching. "
        + "Episodes marked played are left alone. This deletes watch data and cannot be undone.";

    /// <inheritdoc />
    public string Category => "Next Up Cleanup";

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => Array.Empty<TaskTriggerInfo>();

    /// <inheritdoc />
    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null)
        {
            return Task.CompletedTask;
        }

        var threshold = Math.Max(0, config.ResetBelowMinutes) * TimeSpan.TicksPerMinute;
        if (threshold <= 0)
        {
            _logger.LogInformation(
                "Reset abandoned episodes: the mark is set to 0 minutes, which would reset every episode in progress. Doing nothing");
            return Task.CompletedTask;
        }

        var users = GetUsers();
        if (users.Count == 0)
        {
            _logger.LogWarning("Reset abandoned episodes: could not read the server's user list; doing nothing");
            return Task.CompletedTask;
        }

        var reset = 0;
        var examined = 0;

        for (var i = 0; i < users.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var (userExamined, userReset) = ResetForUser(users[i], threshold, cancellationToken);

            examined += userExamined;
            reset += userReset;

            progress.Report((i + 1) * 100.0 / users.Count);
        }

        _logger.LogInformation(
            "Reset abandoned episodes: cleared {Reset} of {Examined} in-progress episode(s) across {Users} user(s), under {Minutes} minute(s)",
            reset,
            examined,
            users.Count,
            config.ResetBelowMinutes);

        return Task.CompletedTask;
    }

    /// <summary>
    /// The server's users, asked for in whichever way this server offers them.
    /// <para>
    /// <c>IUserManager.Users</c> was a property up to 10.11.3 and is a <c>GetUsers()</c>
    /// method from 10.11.11, so a plugin bound to either one at compile time dies with a
    /// <c>MissingMethodException</c> on the other half of the 10.11 series. One binary has
    /// to serve both, and reflection is the only way to ask without binding.
    /// </para>
    /// </summary>
    private IReadOnlyList<User> GetUsers()
    {
        try
        {
            var type = _userManager.GetType();
            var users = type.GetMethod("GetUsers", Type.EmptyTypes)?.Invoke(_userManager, null)
                ?? type.GetProperty("Users")?.GetValue(_userManager);

            if (users is IEnumerable enumerable)
            {
                return enumerable.OfType<User>().ToList();
            }

            _logger.LogWarning(
                "Reset abandoned episodes: {Type} offers neither GetUsers() nor Users",
                type.Name);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Reset abandoned episodes: could not read the user list");
        }

        return Array.Empty<User>();
    }

    private (int Examined, int Reset) ResetForUser(
        User user,
        long threshold,
        CancellationToken cancellationToken)
    {
        var items = _libraryManager.GetItemList(new InternalItemsQuery(user)
        {
            IsResumable = true,
            Recursive = true,
            IncludeItemTypes = new[] { BaseItemKind.Episode },
            MediaTypes = new[] { MediaType.Video },
            IsVirtualItem = false,
            EnableTotalRecordCount = false
        });

        var reset = 0;

        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var userData = _userDataManager.GetUserData(user, item);
            if (userData is null)
            {
                continue;
            }

            // Marked played is real history — an episode watched to the end and started
            // again is not a stray tap, and its play count is worth keeping.
            if (userData.Played)
            {
                continue;
            }

            var position = userData.PlaybackPositionTicks;
            if (position <= 0 || position >= threshold)
            {
                continue;
            }

            userData.PlaybackPositionTicks = 0;
            userData.PlayCount = 0;
            userData.LastPlayedDate = null;

            _userDataManager.SaveUserData(
                user,
                item,
                userData,
                UserDataSaveReason.UpdateUserData,
                cancellationToken);

            _logger.LogDebug(
                "Reset abandoned episodes: cleared {Item} for {User}, {Seconds}s in",
                item.Name,
                user.Username,
                position / TimeSpan.TicksPerSecond);

            reset++;
        }

        return (items.Count, reset);
    }
}
