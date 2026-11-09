using System;
using System.Linq;
using Emby.Server.Implementations.PirateFin.YouTube;
using MediaBrowser.Model.PirateFin;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.PirateFin;

public class YouTubeFeedTests
{
    private static readonly DateTime _now = new(2026, 10, 26, 12, 0, 0, DateTimeKind.Utc);

    private static YouTubeVideo Video(string id, bool subscribed, double daysOld, int seconds = 600) => new()
    {
        Id = id,
        Title = id,
        FromSubscription = subscribed,
        PublishedUtc = _now.AddDays(-daysOld),
        DurationSeconds = seconds
    };

    [Fact]
    public void Build_InterleavesRecentThenOlderSubscribedThenOlderRecommended()
    {
        var videos = new[]
        {
            Video("s1", true, 1), Video("s2", true, 2), Video("s3", true, 3), Video("s4", true, 4),
            Video("r1", false, 1), Video("r2", false, 2),
            Video("sOld", true, 20), Video("rOld", false, 20)
        };

        var feed = YouTubeFeedBuilder.Build(videos, _now, false).Select(v => v.Id);

        Assert.Equal(new[] { "s1", "s2", "s3", "r1", "s4", "r2", "sOld", "rOld" }, feed);
    }

    [Fact]
    public void Build_LeavesOutShortsLiveWatchedAndDuplicates()
    {
        var shortVideo = Video("short", true, 1, 45);
        var tagged = Video("tagged", true, 1, 150);
        tagged.Title = "Wow #Shorts";
        var live = Video("live", true, 1);
        live.IsLive = true;
        var watched = Video("watched", true, 1);
        watched.Watched = true;

        var feed = YouTubeFeedBuilder.Build([shortVideo, tagged, live, watched, Video("ok", true, 1), Video("ok", false, 1)], _now, false);

        Assert.Equal("ok", Assert.Single(feed).Id);
    }

    [Fact]
    public void Build_KidsOnlyShowsMadeForKids()
    {
        var kids = Video("kids", true, 1);
        kids.MadeForKids = true;

        var feed = YouTubeFeedBuilder.Build([kids, Video("adult", true, 1)], _now, true);

        Assert.Equal("kids", Assert.Single(feed).Id);
    }

    [Theory]
    [InlineData("PT1H2M3S", 3723)]
    [InlineData("PT45S", 45)]
    [InlineData("P0D", 0)]
    [InlineData("nonsense", 0)]
    [InlineData(null, 0)]
    public void ParseDurationSeconds_ReadsIsoDurations(string? duration, int expected)
        => Assert.Equal(expected, YouTubeParsing.ParseDurationSeconds(duration));

    [Theory]
    [InlineData("UCBR8-60-B28hp2BmDPdntcQ", "UCBR8-60-B28hp2BmDPdntcQ", null)]
    [InlineData("https://www.youtube.com/channel/UCBR8-60-B28hp2BmDPdntcQ/videos", "UCBR8-60-B28hp2BmDPdntcQ", null)]
    [InlineData("@YouTube", null, "YouTube")]
    [InlineData("https://youtube.com/@some.channel-1", null, "some.channel-1")]
    [InlineData("just words", null, null)]
    public void ParseChannelInput_FindsIdOrHandle(string input, string? channelId, string? handle)
        => Assert.Equal((channelId, handle), YouTubeParsing.ParseChannelInput(input));

    [Theory]
    [InlineData("index.m3u8", true)]
    [InlineData("seg00012.ts", true)]
    [InlineData("../secret.txt", false)]
    [InlineData("seg1.ts", false)]
    [InlineData(null, false)]
    public void IsStreamFile_OnlyAllowsPlaylistAndSegments(string? fileName, bool expected)
        => Assert.Equal(expected, YouTubeStreams.IsStreamFile(fileName));

    [Theory]
    [InlineData("linus tech tips", "UC1")]
    [InlineData("LinusTechTips", "UC1")]
    [InlineData("@mkbhd", "UC2")]
    [InlineData("tech", null)]
    public void MatchChannel_MatchesWholeNameOrHandle(string query, string? expected)
    {
        var channels = new[]
        {
            new YouTubeChannel { Id = "UC1", Title = "Linus Tech Tips", Handle = "@LinusTechTips" },
            new YouTubeChannel { Id = "UC2", Title = "Marques Brownlee", Handle = "@mkbhd" }
        };

        Assert.Equal(expected, YouTubeParsing.MatchChannel(channels, query)?.Id);
    }

    [Theory]
    [InlineData("Linus Tech Tips", "LinusTechTips")]
    [InlineData("@mkbhd", "mkbhd")]
    [InlineData("ab", null)]
    [InlineData("what is a cpu?", null)]
    public void ToHandle_OnlyWhenItCanBeOne(string query, string? expected)
        => Assert.Equal(expected, YouTubeParsing.ToHandle(query));

    [Fact]
    public void GetUploadsPlaylistId_SwapsUcForUu()
        => Assert.Equal("UUBR8-60-B28hp2BmDPdntcQ", YouTubeParsing.GetUploadsPlaylistId("UCBR8-60-B28hp2BmDPdntcQ"));
}
