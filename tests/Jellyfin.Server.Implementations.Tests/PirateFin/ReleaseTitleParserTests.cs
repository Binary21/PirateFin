using Emby.Server.Implementations.PirateFin.Torrents;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.PirateFin;

public class ReleaseTitleParserTests
{
    [Theory]
    [InlineData("The.Show.S02E05.1080p.WEB.h264-ETHEL", 2, 5)]
    [InlineData("The Show S02E05 1080p HEVC x265-MeGusta", 2, 5)]
    [InlineData("The Show s1e12 720p", 1, 12)]
    [InlineData("The Show 3x07 HDTV", 3, 7)]
    [InlineData("[ToonsHub] Frieren S01E05 1080p CR WEB-DL", 1, 5)]
    [InlineData("[SubsPlease] Sousou no Frieren - 05 (1080p) [ABCD1234].mkv", null, 5)]
    [InlineData("[Erai-raws] Sousou no Frieren - 05v2 [1080p][Multiple Subtitle]", null, 5)]
    [InlineData("[SubsPlease] Kaiju No. 8 S2 - 03 (1080p)", 2, 3)]
    [InlineData("[Group] Some Anime 2nd Season - 11 [720p]", 2, 11)]
    [InlineData("Some Anime Episode 4 [1080p]", null, 4)]
    [InlineData("[SubsPlease] Mob Psycho 100 - 05 (1080p)", null, 5)]
    [InlineData("[SubsPlease] Kaiju No. 8 - 12 (1080p)", null, 12)]
    public void Parse_SingleEpisode_ReadsSeasonAndEpisode(string title, int? season, int episode)
    {
        var info = ReleaseTitleParser.Parse(title);

        Assert.False(info.IsPack);
        Assert.Equal(season, info.Season);
        Assert.Equal(episode, info.Episode);
    }

    [Theory]
    [InlineData("[SubsPlease] Sousou no Frieren (01-28) (1080p) [Batch]")]
    [InlineData("The Show S01 1080p WEB-DL")]
    [InlineData("The Show Season 2 Complete 1080p")]
    [InlineData("The Show S01E01-E10 1080p")]
    public void Parse_Packs_AreFlagged(string title)
    {
        Assert.True(ReleaseTitleParser.Parse(title).IsPack);
    }

    [Fact]
    public void Parse_DateInTitle_IsNotARange()
    {
        var info = ReleaseTitleParser.Parse("The Daily Show 2026-10-23 1080p WEB h264");

        Assert.False(info.IsPack);
    }

    [Theory]
    [InlineData("The.Show.S02E05.1080p", 2, 5, true)]
    [InlineData("The.Show.S02E06.1080p", 2, 5, false)]
    [InlineData("The.Show.S01E05.1080p", 2, 5, false)]
    [InlineData("[SubsPlease] Show - 05 (1080p)", 1, 5, true)]
    [InlineData("The Show S02 1080p", 2, 5, false)]
    public void IsEpisode_RequiresExactEpisode(string title, int season, int episode, bool expected)
    {
        Assert.Equal(expected, ReleaseTitleParser.IsEpisode(ReleaseTitleParser.Parse(title), season, episode));
    }

    [Theory]
    [InlineData("The.Show.S02E05.1080p.HEVC.x265-MeGusta", "1080p MeGusta", true)]
    [InlineData("[SubsPlease] Show - 05 (1080p)", "[SubsPlease]", true)]
    [InlineData("[SubsPlease] Show - 05 (1080p)", "[Erai-raws]", false)]
    [InlineData("The.Show.S02E05.720p.HEVC.x265-MeGusta", "1080p MeGusta", false)]
    public void IsByAuthor_MatchesAllAuthorWords(string title, string author, bool expected)
    {
        Assert.Equal(expected, ReleaseTitleParser.IsByAuthor(title, author));
    }

    [Fact]
    public void MentionsTitle_RejectsUnrelatedShow()
    {
        Assert.True(ReleaseTitleParser.MentionsTitle("The.Last.of.Us.S02E05.1080p", "The Last of Us"));
        Assert.False(ReleaseTitleParser.MentionsTitle("Some.Other.Show.S02E05.1080p", "The Last of Us"));
    }

    [Fact]
    public void GetInfoHash_ReadsHexHash()
    {
        Assert.Equal(
            "ABCDEF0123456789ABCDEF0123456789ABCDEF01",
            ReleaseTitleParser.GetInfoHash("magnet:?xt=urn:btih:abcdef0123456789abcdef0123456789abcdef01&dn=x"));
    }
}
