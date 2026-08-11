using Jellyfin.Data.Enums;
using Jellyfin.Plugin.NextUpCleanup.Configuration;
using Jellyfin.Plugin.NextUpCleanup.Filtering;
using MediaBrowser.Model.Dto;
using Xunit;

namespace Jellyfin.Plugin.NextUpCleanup.Tests;

public class NextUpFilterTests
{
    private static BaseItemDto Episode(int season, int number, UserItemDataDto? userData = null)
        => new()
        {
            Type = BaseItemKind.Episode,
            ParentIndexNumber = season,
            IndexNumber = number,
            UserData = userData
        };

    [Theory]
    [InlineData(1, 1, true)]  // the entry Jellyfin 10.11 surfaces for a series you never started
    [InlineData(1, 2, false)] // you are actually watching this one
    [InlineData(1, 12, false)]
    [InlineData(2, 1, false)] // S02E01 means you finished season one
    public void HidesOnlyTheFirstEpisodeOfTheFirstSeason(int season, int number, bool hidden)
        => Assert.Equal(hidden, NextUpFilter.ShouldHide(Episode(season, number), Config(FilterMode.AllFirstEpisodes), EndpointKind.NextUp));

    [Fact]
    public void LeavesNonEpisodesAlone()
    {
        var movie = new BaseItemDto { Type = BaseItemKind.Movie, ParentIndexNumber = 1, IndexNumber = 1 };

        Assert.False(NextUpFilter.ShouldHide(movie, Config(FilterMode.AllFirstEpisodes), EndpointKind.NextUp));
    }

    [Fact]
    public void AnEpisodeWithNoNumbersIsLeftAlone()
    {
        var unnumbered = new BaseItemDto { Type = BaseItemKind.Episode };

        Assert.False(NextUpFilter.ShouldHide(unnumbered, Config(FilterMode.AllFirstEpisodes), EndpointKind.NextUp));
    }

    [Fact]
    public void AllMode_HidesAFirstEpisodeEvenWhenYouAreWellIntoIt()
        => Assert.True(NextUpFilter.ShouldHide(
            Episode(1, 1, Watched(ticks: Minutes(22))),
            Config(FilterMode.AllFirstEpisodes),
            EndpointKind.NextUp));

    // What counts as having watched a first episode, and what only looks like it.
    public static TheoryData<UserItemDataDto, bool> PlayState => new()
    {
        { Watched(ticks: Minutes(20)), true },                                          // well into it
        { Watched(ticks: Minutes(5)), true },                                           // exactly at the threshold
        { Watched(played: true), true },                                                // finished it
        { Watched(ticks: Minutes(0.02)), false },                                       // a second in
        { Watched(ticks: Minutes(2)), false },                                          // under the threshold
        { Watched(playCount: 1), false },                                               // pressed play once
        { Watched(lastPlayed: new DateTime(2026, 7, 1, 20, 0, 0, DateTimeKind.Utc)), false }
    };

    private static PluginConfiguration Config(FilterMode mode, int startedMinutes = 5)
        => new() { Mode = mode, StartedWatchingMinutes = startedMinutes };

    private static long Minutes(double count) => (long)(count * TimeSpan.TicksPerMinute);

    private static UserItemDataDto Watched(
        long ticks = 0,
        int playCount = 0,
        bool played = false,
        DateTime? lastPlayed = null)
        => new()
        {
            Key = "key",
            PlaybackPositionTicks = ticks,
            PlayCount = playCount,
            Played = played,
            LastPlayedDate = lastPlayed
        };

    [Theory]
    [MemberData(nameof(PlayState))]
    public void UntouchedMode_KeepsOnlyAFirstEpisodeYouActuallyWatched(UserItemDataDto userData, bool watched)
        => Assert.Equal(
            !watched,
            NextUpFilter.ShouldHide(Episode(1, 1, userData), Config(FilterMode.UntouchedFirstEpisodes), EndpointKind.NextUp));

    [Fact]
    public void UntouchedMode_HidesAFirstEpisodeWithoutPlayState()
    {
        Assert.True(NextUpFilter.ShouldHide(Episode(1, 1, Watched()), Config(FilterMode.UntouchedFirstEpisodes), EndpointKind.NextUp));
        Assert.True(NextUpFilter.ShouldHide(Episode(1, 1), Config(FilterMode.UntouchedFirstEpisodes), EndpointKind.NextUp));
    }

    [Theory]
    // Stock Jellyfin. One action behind /Shows/NextUp, whatever prefix the client uses.
    [InlineData("TvShows", "GetNextUp", null, EndpointKind.NextUp)]
    // Continue Watching: /UserItems/Resume and the legacy /Users/{id}/Items/Resume.
    [InlineData("Items", "GetResumeItems", null, EndpointKind.Mixed)]
    [InlineData("Items", "GetResumeItemsLegacy", null, EndpointKind.Mixed)]
    // The Home Screen Sections plugin: every row comes from one action, so the section id
    // is what says which row it is.
    [InlineData("HomeScreen", "GetSectionContent", "NextUp", EndpointKind.NextUp)]
    [InlineData("HomeScreen", "GetSectionContent", "ContinueWatching", EndpointKind.Mixed)]
    [InlineData("HomeScreen", "GetSectionContent", "ResumeItems", EndpointKind.Mixed)]
    [InlineData("HomeScreen", "GetSectionContent", "MyMedia", EndpointKind.None)]
    [InlineData("HomeScreen", "GetSectionContent", "LatestMovies", EndpointKind.None)]
    [InlineData("HomeScreen", "GetSectionContent", "LiveTV", EndpointKind.None)]
    [InlineData("HomeScreen", "GetSectionContent", null, EndpointKind.None)]
    // Rows a newly added S01E01 legitimately belongs in.
    [InlineData("TvShows", "GetUpcomingEpisodes", null, EndpointKind.None)]
    [InlineData("UserLibrary", "GetLatestMedia", null, EndpointKind.None)]
    [InlineData("Items", "GetItems", null, EndpointKind.None)]
    [InlineData("HomeScreen", "GetHomeScreenSections", null, EndpointKind.None)]
    [InlineData(null, null, null, EndpointKind.None)]
    internal void RecognisesTheRowActions(string? controller, string? action, string? section, EndpointKind expected)
        => Assert.Equal(expected, NextUpFilter.Classify(controller, action, section));

    private static readonly Guid Friends = Guid.NewGuid();
    private static readonly Guid Himym = Guid.NewGuid();

    private static BaseItemDto Watching(Guid series, string name, int daysAgo)
        => new()
        {
            Name = name,
            Type = BaseItemKind.Episode,
            SeriesId = series,
            SeriesName = series == Friends ? "Friends" : "HIMYM",
            UserData = Watched(ticks: 1, lastPlayed: new DateTime(2026, 8, 11, 0, 0, 0, DateTimeKind.Utc).AddDays(-daysAgo))
        };

    private static string[] Names(IEnumerable<BaseItemDto> items) => items.Select(i => i.Name!).ToArray();

    [Fact]
    public void CollapsesASeriesToItsMostRecentlyPlayedEpisode()
    {
        var row = new[]
        {
            Watching(Friends, "S09E14", 30),
            Watching(Himym, "S02E04", 12),
            Watching(Friends, "S10E08", 2),
            Watching(Himym, "S04E10", 1),
            Watching(Friends, "S09E23", 20)
        };

        var result = NextUpFilter.Deduplicate(row, new PluginConfiguration());

        // One entry per show, and the row's own order is untouched.
        Assert.Equal(new[] { "S10E08", "S04E10" }, Names(result));
    }

    [Fact]
    public void KeepsTheConfiguredNumberOfEpisodesPerSeries()
    {
        var row = new[]
        {
            Watching(Friends, "S09E14", 30),
            Watching(Friends, "S10E08", 2),
            Watching(Friends, "S09E23", 20)
        };

        var result = NextUpFilter.Deduplicate(row, new PluginConfiguration { MaxEpisodesPerSeries = 2 });

        Assert.Equal(new[] { "S10E08", "S09E23" }, Names(result));
    }

    [Fact]
    public void AnEpisodeWithNoPlayDateLosesToOneThatHasIt()
    {
        var never = new BaseItemDto
        {
            Name = "NoDate",
            Type = BaseItemKind.Episode,
            SeriesId = Friends,
            UserData = Watched(ticks: 1)
        };

        var result = NextUpFilter.Deduplicate(new[] { never, Watching(Friends, "S10E08", 400) }, new PluginConfiguration());

        Assert.Equal(new[] { "S10E08" }, Names(result));
    }

    [Fact]
    public void DeduplicationCanBeTurnedOff()
    {
        var row = new[] { Watching(Friends, "S09E14", 30), Watching(Friends, "S10E08", 2) };

        var result = NextUpFilter.Deduplicate(row, new PluginConfiguration { DeduplicateSeries = false });

        Assert.Equal(new[] { "S09E14", "S10E08" }, Names(result));
    }

    [Fact]
    public void EpisodesOfSeriesJellyfinDidNotIdentifyAreGroupedByName()
    {
        // No SeriesId on the DTO — the series name is all there is to group on.
        BaseItemDto Loose(string name, int daysAgo) => new()
        {
            Name = name,
            Type = BaseItemKind.Episode,
            SeriesName = "Some Show",
            UserData = Watched(lastPlayed: new DateTime(2026, 8, 11, 0, 0, 0, DateTimeKind.Utc).AddDays(-daysAgo))
        };

        var result = NextUpFilter.Deduplicate(new[] { Loose("old", 9), Loose("new", 1) }, new PluginConfiguration());

        Assert.Equal(new[] { "new" }, Names(result));
    }

    [Fact]
    public void MoviesAreLeftAloneUnlessAskedFor()
    {
        BaseItemDto Copy(int daysAgo) => new()
        {
            Name = "Dune",
            Type = BaseItemKind.Movie,
            UserData = Watched(lastPlayed: new DateTime(2026, 8, 11, 0, 0, 0, DateTimeKind.Utc).AddDays(-daysAgo))
        };

        var row = new[] { Copy(5), Copy(1) };

        Assert.Equal(2, NextUpFilter.Deduplicate(row, new PluginConfiguration()).Count);
        Assert.Single(NextUpFilter.Deduplicate(row, new PluginConfiguration { DeduplicateMovies = true }));
    }

    [Fact]
    public void TwoDifferentMoviesAreNotDuplicates()
    {
        var row = new[]
        {
            new BaseItemDto { Name = "Dune", Type = BaseItemKind.Movie },
            new BaseItemDto { Name = "Arrival", Type = BaseItemKind.Movie }
        };

        Assert.Equal(2, NextUpFilter.Deduplicate(row, new PluginConfiguration { DeduplicateMovies = true }).Count);
    }

    [Fact]
    internal void ACombinedRowKeepsAnEpisodeYouArePartWayThrough()
    {
        // The one guarantee a Continue Watching row has to make, in either mode.
        var resuming = Episode(1, 1, Watched(ticks: Minutes(22), playCount: 1));

        Assert.False(NextUpFilter.ShouldHide(resuming, Config(FilterMode.AllFirstEpisodes), EndpointKind.Mixed));
        Assert.False(NextUpFilter.ShouldHide(resuming, Config(FilterMode.UntouchedFirstEpisodes), EndpointKind.Mixed));
    }

    [Fact]
    internal void ACombinedRowStillHidesAFirstEpisodeYouAreNotPartWayThrough()
    {
        // The real case from a merged "Weiter ansehen / Als Nächstes" row: a play count
        // and a play date, but no resume position — started and abandoned, or finished and
        // being offered again. Nothing about that is "carry on where you left off".
        var abandoned = Episode(1, 1, Watched(
            playCount: 1,
            lastPlayed: new DateTime(2026, 8, 11, 15, 27, 31, DateTimeKind.Utc)));

        Assert.True(NextUpFilter.ShouldHide(abandoned, Config(FilterMode.AllFirstEpisodes), EndpointKind.Mixed));

        // A watched-through rewatch suggestion is the same story.
        Assert.True(NextUpFilter.ShouldHide(
            Episode(1, 1, Watched(playCount: 3, played: true)),
            Config(FilterMode.AllFirstEpisodes),
            EndpointKind.Mixed));
    }

    [Theory]
    // Under the threshold: a mis-tap or a look at the titles. Not worth continuing, so
    // it goes, on a combined row and in the narrower mode alike.
    [InlineData(0.5, true)]
    [InlineData(2, true)]
    [InlineData(4.9, true)]
    // Past it: genuinely part-way through, and kept.
    [InlineData(5, false)]
    [InlineData(31, false)]
    internal void AResumePositionOnlyCountsPastTheThreshold(double minutesIn, bool hidden)
    {
        var item = Episode(1, 1, Watched(ticks: Minutes(minutesIn), playCount: 1));

        Assert.Equal(hidden, NextUpFilter.ShouldHide(item, Config(FilterMode.AllFirstEpisodes), EndpointKind.Mixed));
        Assert.Equal(hidden, NextUpFilter.ShouldHide(item, Config(FilterMode.UntouchedFirstEpisodes), EndpointKind.Mixed));
        Assert.Equal(!hidden, NextUpFilter.HasStartedWatching(item, Config(FilterMode.AllFirstEpisodes)));
    }

    [Fact]
    internal void TheThresholdIsConfigurable()
    {
        var item = Episode(1, 1, Watched(ticks: Minutes(3)));

        Assert.True(NextUpFilter.ShouldHide(item, Config(FilterMode.AllFirstEpisodes, 5), EndpointKind.Mixed));
        Assert.False(NextUpFilter.ShouldHide(item, Config(FilterMode.AllFirstEpisodes, 2), EndpointKind.Mixed));

        // 0 puts the floor back on the ground: any position at all counts.
        Assert.False(NextUpFilter.ShouldHide(
            Episode(1, 1, Watched(ticks: 1)),
            Config(FilterMode.AllFirstEpisodes, 0),
            EndpointKind.Mixed));
    }

    [Fact]
    internal void APlayCountAloneIsNotStartedWatching()
    {
        // Jellyfin writes a play count and a play date the instant playback begins, so
        // neither says anything about whether the episode was actually watched.
        var pressedOnce = Episode(1, 1, Watched(
            playCount: 1,
            lastPlayed: new DateTime(2026, 8, 11, 15, 27, 31, DateTimeKind.Utc)));

        Assert.False(NextUpFilter.HasStartedWatching(pressedOnce, Config(FilterMode.AllFirstEpisodes)));

        // Marked played is the exception: that is a finished episode, not a stray tap.
        Assert.True(NextUpFilter.HasStartedWatching(
            Episode(1, 1, Watched(played: true)),
            Config(FilterMode.AllFirstEpisodes)));
    }

    [Theory]
    // The merged section the Home Screen Sections "combine Continue Watching and Next Up"
    // option serves. It carries resumable items, so it is a combined row.
    [InlineData("ContinueWatchingNextUp", EndpointKind.Mixed)]
    [InlineData("NextUpContinueWatching", EndpointKind.Mixed)]
    internal void RecognisesTheMergedSection(string section, EndpointKind expected)
        => Assert.Equal(expected, NextUpFilter.Classify("HomeScreen", "GetSectionContent", section));
}
