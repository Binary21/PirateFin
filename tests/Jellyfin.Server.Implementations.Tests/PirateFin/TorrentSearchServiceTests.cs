using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Emby.Server.Implementations.PirateFin.Torrents;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.PirateFin;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.PirateFin;

public class TorrentSearchServiceTests
{
    private readonly JackettOptions _options = new()
    {
        Indexer = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Shows"] = "knaben", ["Anime"] = "nyaasi" },
        ShowAuthors = ["1080p MeGusta", "1080p EDITH"],
        AnimeSubGroups = [new() { Name = "[ToonsHub]" }, new() { Name = "[SubsPlease]" }],
        AnimeDubGroups = [new() { Name = "Yameii", Indicator = "English Dub" }],
        ShowCategory = "5040",
        AnimeCategory = "5070"
    };

    private readonly Mock<JackettClient> _jackett;
    private readonly TorrentSearchService _service;
    private readonly List<string> _queries = [];
    private readonly Dictionary<string, List<TorrentCandidate>> _results = new(StringComparer.OrdinalIgnoreCase);

    public TorrentSearchServiceTests()
    {
        var monitor = Mock.Of<IOptionsMonitor<JackettOptions>>(m => m.CurrentValue == _options);
        _jackett = new Mock<JackettClient>(Mock.Of<IHttpClientFactory>(), monitor, NullLogger<JackettClient>.Instance);
        _jackett
            .Setup(j => j.SearchAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string query, string _, string _, CancellationToken _) =>
            {
                _queries.Add(query);
                return _results.TryGetValue(query, out var list) ? list : [];
            });
        _service = new TorrentSearchService(_jackett.Object, monitor, NullLogger<TorrentSearchService>.Instance);
    }

    private static TorrentCandidate Release(string title, int seeders)
        => new() { Title = title, MagnetUrl = $"magnet:?xt=urn:btih:{Math.Abs(title.GetHashCode(StringComparison.Ordinal)):D40}", Seeders = seeders };

    private static Episode TvEpisode(int season, int number) => new()
    {
        SeriesName = "The Last of Us",
        LibraryType = "Shows",
        ParentIndexNumber = season,
        IndexNumber = number
    };

    private static Episode AnimeEpisode(int season, int number, string romaji) => new()
    {
        SeriesName = "Frieren",
        AlternateTitle = romaji,
        LibraryType = "Anime",
        ParentIndexNumber = season,
        IndexNumber = number
    };

    [Fact]
    public async Task Author_PicksExactEpisodeFromAuthor_IgnoresOtherEpisodesAndGroups()
    {
        _results["1080p MeGusta The Last of Us S02E05"] =
        [
            Release("The.Last.of.Us.S02E04.1080p.HEVC.x265-MeGusta", 900),
            Release("The.Last.of.Us.S02E05.720p.HEVC.x265-MeGusta", 800),
            Release("The.Last.of.Us.S02E05.1080p.HEVC.x265-MeGusta", 100),
            Release("The.Last.of.Us.S02E05.1080p.WEB.h264-OTHER", 5000)
        ];

        var release = await _service.FindAuthorReleaseAsync(TvEpisode(2, 5), CancellationToken.None);

        Assert.NotNull(release);
        Assert.Equal("The.Last.of.Us.S02E05.1080p.HEVC.x265-MeGusta", release!.Title);
        Assert.Equal("1080p MeGusta", release.Author);
    }

    [Fact]
    public async Task Author_FallsThroughAuthorsInOrder()
    {
        _results["1080p EDITH The Last of Us S02E05"] = [Release("The.Last.of.Us.S02E05.1080p.WEB.h264-EDITH", 40)];

        var release = await _service.FindAuthorReleaseAsync(TvEpisode(2, 5), CancellationToken.None);

        Assert.Equal("1080p EDITH", release?.Author);
        Assert.Equal(["1080p MeGusta The Last of Us S02E05", "1080p EDITH The Last of Us S02E05"], _queries);
    }

    [Fact]
    public async Task Author_AnimeFansubNaming_IsFound()
    {
        _results["[SubsPlease] Frieren 05"] = [Release("[SubsPlease] Frieren - 05 (1080p) [ABCD1234].mkv", 300)];

        var release = await _service.FindAuthorReleaseAsync(AnimeEpisode(1, 5, "Sousou no Frieren"), CancellationToken.None);

        Assert.Equal("[SubsPlease]", release?.Author);
    }

    [Fact]
    public async Task Dub_IndicatorGoesBeforeTheEpisode_AndMustBeInTheRelease()
    {
        _results["Yameii Frieren English Dub 05"] =
        [
            Release("[Yameii] Frieren - 05 [Japanese Audio] [1080p]", 900),
            Release("[Yameii] Frieren - 05 [English Dub] [1080p]", 100)
        ];

        var release = await _service.FindAuthorReleaseAsync(AnimeEpisode(1, 5, "Sousou no Frieren"), ReleaseLanguage.Dub, CancellationToken.None);

        Assert.Equal("[Yameii] Frieren - 05 [English Dub] [1080p]", release?.Title);
        Assert.Equal(ReleaseLanguage.Dub, release?.Language);
        Assert.Equal(["Yameii Frieren English Dub S01E05", "Yameii Frieren English Dub 05"], _queries);
    }

    [Fact]
    public async Task Dub_ReleaseWithoutIndicator_IsRejected()
    {
        _results["Yameii Frieren English Dub 05"] = [Release("[Yameii] Frieren - 05 [1080p]", 900)];

        Assert.Null(await _service.FindAuthorReleaseAsync(AnimeEpisode(1, 5, "Sousou no Frieren"), ReleaseLanguage.Dub, CancellationToken.None));
    }

    [Fact]
    public async Task Sub_SearchesOnlyTheSubGroups()
    {
        await _service.FindAuthorReleaseAsync(AnimeEpisode(1, 5, "Sousou no Frieren"), ReleaseLanguage.Sub, CancellationToken.None);

        Assert.DoesNotContain(_queries, q => q.Contains("Yameii", StringComparison.Ordinal));
        Assert.Contains("[SubsPlease] Frieren 05", _queries);
    }

    [Fact]
    public async Task TitleOverride_ReplacesEveryDefaultTitle()
    {
        var series = new Series { Name = "Frieren", SearchTitleOverride = "Sousou no Frieren!" };

        await _service.FindAuthorReleaseAsync(AnimeEpisode(1, 5, "Frieren Beyond"), series, null, ReleaseLanguage.Sub, CancellationToken.None);

        Assert.All(_queries, q => Assert.Contains(" Sousou no Frieren ", q, StringComparison.Ordinal));
        Assert.DoesNotContain(_queries, q => q.Contains("Beyond", StringComparison.Ordinal));
        Assert.Equal(4, _queries.Count); // 2 groups x (S01E05, 05)
    }

    [Fact]
    public async Task SeasonNumberOverride_IsSearchedAndMatched()
    {
        var season = new Season { IndexNumber = 2, SearchSeasonNumberOverride = 1 };
        _results["[SubsPlease] Frieren S01E05"] = [Release("[SubsPlease] Frieren S01E05 (1080p)", 300)];

        var release = await _service.FindAuthorReleaseAsync(AnimeEpisode(2, 5, "Sousou no Frieren 2"), null, season, ReleaseLanguage.Sub, CancellationToken.None);

        Assert.NotNull(release);
        Assert.Contains("[ToonsHub] Frieren S01E05", _queries);
    }

    [Fact]
    public void MigrateAnimeAuthors_MovesTheOldListIntoSubs()
    {
        var options = new JackettOptions { AnimeAuthors = ["[SubsPlease]", " [Erai-raws] ", string.Empty] };

        options.MigrateAnimeAuthors();

        Assert.Equal(["[SubsPlease]", "[Erai-raws]"], options.AnimeSubGroups.Select(g => g.Name));
        Assert.All(options.AnimeSubGroups, g => Assert.Empty(g.Indicator));
        Assert.Empty(options.AnimeAuthors);
    }

    [Fact]
    public void MigrateAnimeAuthors_KeepsAnExistingSubList()
    {
        var options = new JackettOptions { AnimeAuthors = ["[Old]"], AnimeSubGroups = [new() { Name = "[New]" }] };

        options.MigrateAnimeAuthors();

        Assert.Equal(["[New]"], options.AnimeSubGroups.Select(g => g.Name));
    }

    [Fact]
    public async Task Author_LaterSeason_SeasonlessReleaseOnlyCountsUnderSeasonTitle()
    {
        // Season 1's "Frieren - 05" must not be taken for S02E05...
        _results["[SubsPlease] Frieren 05"] = [Release("[SubsPlease] Frieren - 05 (1080p)", 300)];
        Assert.Null(await _service.FindAuthorReleaseAsync(AnimeEpisode(2, 5, "Sousou no Frieren 2"), CancellationToken.None));

        // ...but a release named after the season's own AniList title is.
        _results["[SubsPlease] Sousou no Frieren 2 05"] = [Release("[SubsPlease] Sousou no Frieren 2 - 05 (1080p)", 300)];
        Assert.NotNull(await _service.FindAuthorReleaseAsync(AnimeEpisode(2, 5, "Sousou no Frieren 2"), CancellationToken.None));
    }

    [Fact]
    public async Task Fallback_IsTopFiveBySeeders_WithoutAuthor_ExcludingOtherContent()
    {
        _results["The Last of Us S02E05"] =
        [
            Release("The.Last.of.Us.S02E05.2160p.WEB-A", 10),
            Release("The.Last.of.Us.S02E05.1080p.WEB-B", 70),
            Release("The.Last.of.Us.S02E05.720p.WEB-C", 30),
            Release("The.Last.of.Us.S02E05.480p.WEB-D", 20),
            Release("The.Last.of.Us.S02E05.1080p.x265-E", 60),
            Release("The.Last.of.Us.S02E05.1080p.AMZN-F", 50),
            Release("The.Last.of.Us.S02E06.1080p.WEB-G", 999),
            Release("The.Last.of.Us.S02.1080p.WEB-PACK", 999),
            Release("Some.Other.Show.S02E05.1080p", 999)
        ];

        var fallback = await _service.FindFallbackReleasesAsync(TvEpisode(2, 5), 5, CancellationToken.None);

        Assert.Equal([70, 60, 50, 30, 20], fallback.Select(r => r.Seeders));
        Assert.All(fallback, r => Assert.Null(r.Author));
        Assert.Equal(["The Last of Us S02E05"], _queries);
    }

    [Fact]
    public async Task Fallback_DeduplicatesSameTorrentFoundUnderSeveralTitles()
    {
        var release = Release("[SubsPlease] Sousou no Frieren - 05 (1080p)", 300);
        _results["Frieren 05"] = [release];
        _results["Sousou no Frieren 05"] = [release with { Seeders = 310 }];

        var fallback = await _service.FindFallbackReleasesAsync(AnimeEpisode(1, 5, "Sousou no Frieren"), 5, CancellationToken.None);

        Assert.Single(fallback);
        Assert.Equal(310, fallback[0].Seeders);
    }

    [Fact]
    public async Task UnknownLibrary_DoesNotSearch()
    {
        var episode = TvEpisode(1, 1);
        episode.LibraryType = "Movies";

        Assert.Null(await _service.FindAuthorReleaseAsync(episode, CancellationToken.None));
        Assert.Empty(_queries);
    }
}
