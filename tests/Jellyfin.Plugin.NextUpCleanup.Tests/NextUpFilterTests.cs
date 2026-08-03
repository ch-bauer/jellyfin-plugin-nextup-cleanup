using System.Text.Json.Nodes;
using Jellyfin.Plugin.NextUpCleanup.Configuration;
using Jellyfin.Plugin.NextUpCleanup.Filtering;
using Jellyfin.Plugin.NextUpCleanup.Middleware;
using Xunit;

namespace Jellyfin.Plugin.NextUpCleanup.Tests;

public class NextUpFilterTests
{
    private static string Body(params string[] items)
        => $"{{\"Items\":[{string.Join(',', items)}],\"TotalRecordCount\":{items.Length},\"StartIndex\":0}}";

    private static string Episode(int season, int number, string name = "Ep", string? userData = null)
        => $"{{\"Name\":\"{name}\",\"Type\":\"Episode\",\"ParentIndexNumber\":{season},\"IndexNumber\":{number}"
            + (userData is null ? string.Empty : $",\"UserData\":{userData}")
            + "}";

    private static string[] Names(string json)
        => JsonNode.Parse(json)!["Items"]!.AsArray().Select(i => i!["Name"]!.GetValue<string>()).ToArray();

    private static int Total(string json)
        => JsonNode.Parse(json)!["TotalRecordCount"]!.GetValue<int>();

    [Fact]
    public void HidesFirstEpisodes()
    {
        var json = Body(
            Episode(1, 1, "Bogus"),
            Episode(3, 4, "Real"),
            Episode(1, 1, "AlsoBogus"));

        var result = NextUpFilter.Apply(json, new PluginConfiguration(), null, out var hidden);

        Assert.Equal(2, hidden);
        Assert.Equal(new[] { "Real" }, Names(result));
    }

    [Fact]
    public void KeepsLaterEpisodesOfSeasonOne()
    {
        var json = Body(Episode(1, 2, "S01E02"), Episode(1, 12, "S01E12"));

        var result = NextUpFilter.Apply(json, new PluginConfiguration(), null, out var hidden);

        Assert.Equal(0, hidden);
        Assert.Equal(json, result);
    }

    [Fact]
    public void KeepsFirstEpisodeOfALaterSeason()
    {
        // S02E01 is a legitimate next up: it means season one is finished.
        var json = Body(Episode(2, 1, "S02E01"));

        NextUpFilter.Apply(json, new PluginConfiguration(), null, out var hidden);

        Assert.Equal(0, hidden);
    }

    [Fact]
    public void LeavesNonEpisodesAlone()
    {
        var json = "{\"Items\":[{\"Name\":\"A Film\",\"Type\":\"Movie\",\"ParentIndexNumber\":1,\"IndexNumber\":1}],\"TotalRecordCount\":1}";

        NextUpFilter.Apply(json, new PluginConfiguration(), null, out var hidden);

        Assert.Equal(0, hidden);
    }

    [Theory]
    [InlineData("{\"PlaybackPositionTicks\":123456}")]
    [InlineData("{\"PlayCount\":1}")]
    [InlineData("{\"Played\":true}")]
    [InlineData("{\"LastPlayedDate\":\"2026-07-01T20:00:00.0000000Z\"}")]
    public void UntouchedMode_KeepsAFirstEpisodeWithPlayState(string userData)
    {
        var config = new PluginConfiguration { Mode = FilterMode.UntouchedFirstEpisodes };
        var json = Body(Episode(1, 1, "Started", userData));

        NextUpFilter.Apply(json, config, null, out var hidden);

        Assert.Equal(0, hidden);
    }

    [Fact]
    public void UntouchedMode_HidesAFirstEpisodeWithoutPlayState()
    {
        var config = new PluginConfiguration { Mode = FilterMode.UntouchedFirstEpisodes };
        var json = Body(
            Episode(1, 1, "Never", "{\"PlaybackPositionTicks\":0,\"PlayCount\":0,\"Played\":false}"),
            Episode(1, 1, "NoUserData"));

        NextUpFilter.Apply(json, config, null, out var hidden);

        Assert.Equal(2, hidden);
    }

    [Fact]
    public void TrimsBackToTheRequestedLimit()
    {
        // What an over-fetched request looks like: the client asked for 2, we asked for 6.
        var json = Body(
            Episode(1, 1, "Bogus"),
            Episode(2, 3, "A"),
            Episode(4, 5, "B"),
            Episode(6, 7, "C"),
            Episode(8, 9, "D"),
            Episode(1, 1, "Bogus2"));

        var result = NextUpFilter.Apply(json, new PluginConfiguration(), 2, out var hidden);

        Assert.Equal(new[] { "A", "B" }, Names(result));
        Assert.Equal(1, hidden); // only the one seen before the page filled up
    }

    [Fact]
    public void TotalRecordCountNeverUndercountsWhatIsReturned()
    {
        var json = Body(Episode(1, 1, "Bogus"), Episode(2, 1, "Keep"));

        var result = NextUpFilter.Apply(json, new PluginConfiguration(), null, out _);

        Assert.Equal(1, Total(result));
        Assert.Single(Names(result));
    }

    [Fact]
    public void UnchangedBodyIsReturnedByReferenceWhenNothingIsHidden()
    {
        var json = Body(Episode(5, 5, "Keep"));

        Assert.Same(json, NextUpFilter.Apply(json, new PluginConfiguration(), null, out _));
    }

    [Fact]
    public void EmptyResultIsLeftAlone()
    {
        const string Json = "{\"Items\":[],\"TotalRecordCount\":0}";

        Assert.Same(Json, NextUpFilter.Apply(Json, new PluginConfiguration(), null, out var hidden));
        Assert.Equal(0, hidden);
    }

    [Theory]
    [InlineData("/Shows/NextUp", true)]
    [InlineData("/shows/nextup", true)]
    [InlineData("/HomeScreen/Section/NextUp", true)]
    [InlineData("/HomeScreen/Section/NextUpEnhanced", true)]
    [InlineData("/Shows/Upcoming", false)]
    [InlineData("/Users/abc/Items/Resume", false)]
    [InlineData("/Shows/NextUp/Extra", false)]
    [InlineData("", false)]
    public void RecognisesTheNextUpEndpoints(string path, bool expected)
        => Assert.Equal(expected, NextUpFilterMiddleware.IsNextUpEndpoint(path));
}
