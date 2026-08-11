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
        => Assert.Equal(hidden, NextUpFilter.ShouldHide(Episode(season, number), FilterMode.AllFirstEpisodes));

    [Fact]
    public void LeavesNonEpisodesAlone()
    {
        var movie = new BaseItemDto { Type = BaseItemKind.Movie, ParentIndexNumber = 1, IndexNumber = 1 };

        Assert.False(NextUpFilter.ShouldHide(movie, FilterMode.AllFirstEpisodes));
    }

    [Fact]
    public void AnEpisodeWithNoNumbersIsLeftAlone()
    {
        var unnumbered = new BaseItemDto { Type = BaseItemKind.Episode };

        Assert.False(NextUpFilter.ShouldHide(unnumbered, FilterMode.AllFirstEpisodes));
    }

    [Fact]
    public void AllMode_HidesAFirstEpisodeEvenWhenItHasPlayState()
        => Assert.True(NextUpFilter.ShouldHide(Episode(1, 1, Watched(ticks: 123456)), FilterMode.AllFirstEpisodes));

    public static TheoryData<UserItemDataDto> PlayState => new()
    {
        Watched(ticks: 123456),
        Watched(playCount: 1),
        Watched(played: true),
        Watched(lastPlayed: new DateTime(2026, 7, 1, 20, 0, 0, DateTimeKind.Utc))
    };

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
    public void UntouchedMode_KeepsAFirstEpisodeWithPlayState(UserItemDataDto userData)
        => Assert.False(NextUpFilter.ShouldHide(Episode(1, 1, userData), FilterMode.UntouchedFirstEpisodes));

    [Fact]
    public void UntouchedMode_HidesAFirstEpisodeWithoutPlayState()
    {
        Assert.True(NextUpFilter.ShouldHide(Episode(1, 1, Watched()), FilterMode.UntouchedFirstEpisodes));
        Assert.True(NextUpFilter.ShouldHide(Episode(1, 1), FilterMode.UntouchedFirstEpisodes));
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

    [Theory]
    [InlineData(EndpointKind.NextUp, FilterMode.AllFirstEpisodes, FilterMode.AllFirstEpisodes)]
    [InlineData(EndpointKind.NextUp, FilterMode.UntouchedFirstEpisodes, FilterMode.UntouchedFirstEpisodes)]
    // A row that is genuinely in progress must not lose an episode you are part-way
    // through, whichever mode is configured.
    [InlineData(EndpointKind.Mixed, FilterMode.AllFirstEpisodes, FilterMode.UntouchedFirstEpisodes)]
    [InlineData(EndpointKind.Mixed, FilterMode.UntouchedFirstEpisodes, FilterMode.UntouchedFirstEpisodes)]
    internal void NarrowsTheModeOnACombinedRow(EndpointKind kind, FilterMode configured, FilterMode expected)
        => Assert.Equal(expected, NextUpFilter.EffectiveMode(configured, kind));
}
