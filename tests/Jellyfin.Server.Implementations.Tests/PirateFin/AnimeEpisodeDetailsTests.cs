using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Emby.Server.Implementations.PirateFin.Library;
using Emby.Server.Implementations.PirateFin.Providers;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.PirateFin.Models;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.PirateFin;

/// <summary>
/// Anime episodes get their TMDB details by air date (TMDB numbers seasons differently); the season the user is looking
/// at is refreshed ahead of the queue.
/// </summary>
public sealed class AnimeEpisodeDetailsTests
{
    private static readonly DateTime _start = new(2026, 10, 3, 15, 30, 0, DateTimeKind.Utc);

    // TMDB: one long season; the AniList season (second cour) starts at TMDB episode 13, a day later in Japan.
    private static List<TmdbEpisode> TmdbShow(int count = 24, int cour2Start = 13)
        => Enumerable.Range(1, count)
            .Select(n => new TmdbEpisode(
                1,
                n,
                n == count ? "Episode " + n : "Title " + n,
                "Overview " + n,
                _start.Date.AddDays(1 + (7 * (n - cour2Start))),
                "https://image.tmdb.org/t/p/original/" + n + ".jpg"))
            .ToList();

    private static SearchedSeason AniListSeason(int episodes, bool withUpcomingUndated = false)
    {
        var season = new SearchedSeason { Season = 2 };
        for (var n = 1; n <= episodes; n++)
        {
            season.Episodes.Add(new SearchedEpisode { Episode = n, FirstAiring = withUpcomingUndated && n == episodes ? null : _start.AddDays(7 * (n - 1)) });
        }

        return season;
    }

    [Fact]
    public void MatchesByAirDate_AcrossDifferentNumbering()
    {
        var season = AniListSeason(12);

        var matched = AnimeEpisodeDetails.Match(season, TmdbShow());

        Assert.Equal(12, matched);
        Assert.Equal("Title 13", season.Episodes[0].Title);
        Assert.Equal("Overview 18", season.Episodes[5].Overview);
        Assert.EndsWith("/23.jpg", season.Episodes[10].ImageUrl, StringComparison.Ordinal);

        // TMDB's "Episode 24" placeholder name isn't a title.
        Assert.Equal(string.Empty, season.Episodes[11].Title);
        Assert.Equal("Overview 24", season.Episodes[11].Overview);
    }

    [Fact]
    public void UndatedEpisodes_GetTmdbsAirDate()
    {
        var season = AniListSeason(12, withUpcomingUndated: true);

        AnimeEpisodeDetails.Match(season, TmdbShow());

        Assert.Equal(_start.Date.AddDays(1 + 77), season.Episodes[11].FirstAiring);
    }

    [Fact]
    public void NoDateInCommon_MatchesNothing()
    {
        var season = AniListSeason(12);

        Assert.Equal(0, AnimeEpisodeDetails.Match(season, TmdbShow(cour2Start: 40)));
        Assert.All(season.Episodes, e => Assert.Equal(string.Empty, e.Title));
    }

    [Fact]
    public void KeepsWhatAniListHas()
    {
        var season = AniListSeason(2);
        season.Episodes[0].Title = "Own title";

        AnimeEpisodeDetails.Match(season, TmdbShow());

        Assert.Equal("Own title", season.Episodes[0].Title);
        Assert.Equal("Title 14", season.Episodes[1].Title);
    }

    [Fact]
    public async Task RefreshQueue_TheViewedSeasonGoesFirst()
    {
        var items = Enumerable.Range(0, 21).Select(n => new Video { Id = Guid.NewGuid(), Name = n.ToString(System.Globalization.CultureInfo.InvariantCulture) }).ToList();
        var libraryManager = new Mock<ILibraryManager>();
        libraryManager.Setup(l => l.GetItemById(It.IsAny<Guid>())).Returns((Guid id) => items.First(i => i.Id.Equals(id)));

        var order = new List<string>();
        var done = new TaskCompletionSource();
        var providerManager = new Mock<IProviderManager>();
        providerManager.Setup(p => p.RefreshFullItem(It.IsAny<BaseItem>(), It.IsAny<MetadataRefreshOptions>(), It.IsAny<CancellationToken>()))
            .Returns((BaseItem item, MetadataRefreshOptions _, CancellationToken _) =>
            {
                lock (order)
                {
                    order.Add(item.Name);
                    if (order.Count == items.Count)
                    {
                        done.TrySetResult();
                    }
                }

                return Task.CompletedTask;
            });

        using var queue = new MetadataRefreshQueue(libraryManager.Object, providerManager.Object, Mock.Of<IFileSystem>(), NullLogger<MetadataRefreshQueue>.Instance);
        queue.Enqueue(null, items.Take(20).ToList());
        queue.EnqueueFirst(null, [items[20]]);

        await queue.StartAsync(TestContext.Current.CancellationToken);
        await done.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await queue.StopAsync(TestContext.Current.CancellationToken);

        Assert.Equal("20", order[0]);
    }
}
