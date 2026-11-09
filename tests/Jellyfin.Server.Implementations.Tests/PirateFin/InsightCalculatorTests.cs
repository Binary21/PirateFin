using System;
using System.Collections.Generic;
using System.Linq;
using Emby.Server.Implementations.PirateFin.Insights;
using MediaBrowser.Model.PirateFin;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.PirateFin;

/// <summary>
/// The Insights tab's numbers: watch time spread over the hours it happened, streaks, top titles and the hints.
/// </summary>
public sealed class InsightCalculatorTests
{
    private static readonly DateTime _now = new(2026, 10, 25, 22, 0, 0, DateTimeKind.Utc);

    private static PlaybackSessionRecord Session(DateTime startUtc, TimeSpan length, long watchedSeconds, Guid? seriesId = null, string name = "Item") => new()
    {
        ItemId = Guid.NewGuid(),
        SeriesId = seriesId,
        SeriesName = seriesId.HasValue ? "Show" : null,
        ItemName = name,
        ItemType = seriesId.HasValue ? "Episode" : "Movie",
        StartUtc = startUtc,
        EndUtc = startUtc + length,
        WatchedSeconds = watchedSeconds
    };

    private static List<DailyWatchTime> Days(params long[] seconds)
        => seconds.Select((s, i) => new DailyWatchTime { Date = i.ToString(System.Globalization.CultureInfo.InvariantCulture), Seconds = s }).ToList();

    [Fact]
    public void SpreadOverHours_SplitsASessionAcrossHours_InLocalTime()
    {
        // 20:30-21:30 UTC is 16:30-17:30 at UTC-4; 3600 s watched, half in each hour.
        var session = Session(new DateTime(2026, 10, 24, 20, 30, 0, DateTimeKind.Utc), TimeSpan.FromHours(1), 3600);

        var hours = InsightCalculator.SpreadOverHours([session], TimeSpan.FromHours(-4));

        Assert.Equal(1800, hours[new DateTime(2026, 10, 24, 16, 0, 0)], 3);
        Assert.Equal(1800, hours[new DateTime(2026, 10, 24, 17, 0, 0)], 3);
    }

    [Fact]
    public void GetDaily_CoversNinetyDaysEndingToday()
    {
        var hours = new Dictionary<DateTime, double> { [new DateTime(2026, 10, 25, 9, 0, 0)] = 120, [new DateTime(2026, 10, 25, 10, 0, 0)] = 60 };

        var daily = InsightCalculator.GetDaily(hours, new DateTime(2026, 10, 25));

        Assert.Equal(InsightCalculator.Days, daily.Count);
        Assert.Equal("2026-10-25", daily[^1].Date);
        Assert.Equal(180, daily[^1].Seconds);
        Assert.Equal(0, daily[0].Seconds);
    }

    [Fact]
    public void GetHeatmap_IsWeekdayByHour()
    {
        // 25 Oct 2026 is a Sunday.
        var heatmap = InsightCalculator.GetHeatmap(new Dictionary<DateTime, double> { [new DateTime(2026, 10, 25, 21, 0, 0)] = 900 });

        Assert.Equal(7, heatmap.Count);
        Assert.All(heatmap, row => Assert.Equal(24, row.Count));
        Assert.Equal(900, heatmap[0][21]);
    }

    [Theory]
    [InlineData(new long[] { 0, 600, 600, 600 }, 3)]
    [InlineData(new long[] { 600, 600, 600, 0 }, 3)] // nothing yet today: the streak up to yesterday still counts
    [InlineData(new long[] { 600, 0, 600, 30 }, 1)] // under a minute isn't a day watched
    [InlineData(new long[] { 0, 0 }, 0)]
    public void GetStreak_CountsDaysInARow(long[] seconds, int expected)
    {
        Assert.Equal(expected, InsightCalculator.GetStreak(Days(seconds)));
    }

    [Fact]
    public void GetTopTitles_GroupsEpisodesBySeries()
    {
        var show = Guid.NewGuid();
        var sessions = new[]
        {
            Session(_now.AddDays(-1), TimeSpan.FromMinutes(40), 2400, show),
            Session(_now.AddDays(-2), TimeSpan.FromMinutes(40), 2400, show),
            Session(_now.AddDays(-3), TimeSpan.FromHours(2), 3600, name: "A Movie")
        };

        var top = InsightCalculator.GetTopTitles(sessions, 10);

        Assert.Equal(["Show", "A Movie"], top.Select(t => t.Name));
        Assert.Equal(4800, top[0].Seconds);
        Assert.Equal(2, top[0].Episodes);
        Assert.Equal("Series", top[0].Type);
        Assert.Equal("Movie", top[1].Type);
    }

    [Fact]
    public void GetSuggestions_PointsOutThinGenresSearchesDropOffsRequestsAndInactivity()
    {
        var genres = new[]
        {
            new GenreRanking
            {
                Library = "Shows",
                Genres = [new RankedGenre { Name = "Thriller", Titles = 6 }, new RankedGenre { Name = "Drama", Titles = 4 }, new RankedGenre { Name = "Comedy", Titles = 1 }]
            }
        };
        var libraryTitles = new Dictionary<string, IReadOnlyDictionary<string, int>>
        {
            ["Shows"] = new Dictionary<string, int> { ["Thriller"] = 4, ["Drama"] = 80, ["Comedy"] = 2 }
        };
        var searches = new[]
        {
            new SearchRecord { Library = "Anime", Query = "Frieren", Utc = _now.AddDays(-2) },
            new SearchRecord { Library = "Anime", Query = "frieren", Utc = _now.AddDays(-3) }
        };
        var dropOffs = new[]
        {
            new InsightTitle { Name = "A", Genres = ["Comedy"] },
            new InsightTitle { Name = "B", Genres = ["Comedy", "Drama"] }
        };
        var requests = new[]
        {
            new DownloadRequest { SeriesName = "Old Show", Title = "Season 2", Status = DownloadRequestStatus.Open, CreatedUtc = _now.AddDays(-9) },
            new DownloadRequest { SeriesName = "New Show", Title = "Season 1", Status = DownloadRequestStatus.Open, CreatedUtc = _now.AddDays(-1) }
        };

        var suggestions = InsightCalculator.GetSuggestions(genres, libraryTitles, searches, dropOffs, requests, _now.AddDays(-20), _now);

        // Drama has plenty of titles and Comedy isn't watched enough to count.
        var thin = Assert.Single(suggestions, s => s.Kind == "ThinGenre");
        Assert.Contains("Thriller", thin.Text, StringComparison.Ordinal);
        Assert.Contains("only has 4", thin.Text, StringComparison.Ordinal);

        // The same search typed twice is one hint.
        Assert.Contains("\"Frieren\"", Assert.Single(suggestions, s => s.Kind == "NothingFound").Text, StringComparison.Ordinal);
        Assert.Contains("2 Comedy", Assert.Single(suggestions, s => s.Kind == "GenreDropOffs").Text, StringComparison.Ordinal);
        Assert.Contains("Old Show", Assert.Single(suggestions, s => s.Kind == "WaitingRequest").Text, StringComparison.Ordinal);
        Assert.Contains("20 days", Assert.Single(suggestions, s => s.Kind == "Inactive").Text, StringComparison.Ordinal);
    }

    [Fact]
    public void GetSuggestions_NothingToSay_IsEmpty()
    {
        var suggestions = InsightCalculator.GetSuggestions([], new Dictionary<string, IReadOnlyDictionary<string, int>>(), [], [], [], _now.AddHours(-3), _now);

        Assert.Empty(suggestions);
    }
}
