using System;
using System.Collections.Generic;
using System.Linq;
using Emby.Server.Implementations.PirateFin.Insights;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.PirateFin;

/// <summary>
/// Genres are ranked by titles watched: a movie once played or watched halfway, a series once three episodes are
/// played. Discovery genres are what the other users watch, outside this user's top half.
/// </summary>
public sealed class GenreRankingServiceTests
{
    private readonly Guid _folderId = Guid.NewGuid();
    private readonly Mock<ILibraryManager> _libraryManager = new();
    private readonly Mock<IUserDataManager> _userData = new();
    private readonly Mock<IUserManager> _userManager = new();
    private readonly User _viewer = new("viewer", "default", "default");
    private readonly User _other = new("other", "default", "default");
    private readonly Dictionary<Guid, Watched> _watched = [];
    private readonly GenreRankingService _service;

    public GenreRankingServiceTests()
    {
        _watched[_viewer.Id] = new Watched();
        _watched[_other.Id] = new Watched();

        _userManager.Setup(u => u.GetUserById(_viewer.Id)).Returns(_viewer);
        _userManager.Setup(u => u.GetUsers()).Returns([_viewer, _other]);
        _libraryManager.Setup(l => l.GetVirtualFolders()).Returns([new VirtualFolderInfo { Name = "Shows", ItemId = _folderId.ToString() }]);
        _libraryManager.Setup(l => l.GetGenre(It.IsAny<string>())).Returns((string name) => new Genre { Id = Guid.NewGuid(), Name = name });
        _libraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>())).Returns((InternalItemsQuery q) => Query(q));
        _userData.Setup(d => d.GetUserData(It.IsAny<User>(), It.IsAny<BaseItem>()))
            .Returns((User user, BaseItem item) => new UserItemData { Key = "k", PlaybackPositionTicks = _watched[user.Id].Positions.GetValueOrDefault(item.Id) });

        _service = new GenreRankingService(_libraryManager.Object, _userManager.Object, _userData.Object, TimeProvider.System, NullLogger<GenreRankingService>.Instance);
    }

    private IReadOnlyList<BaseItem> Query(InternalItemsQuery q)
    {
        if (q.ItemIds.Length > 0)
        {
            return _watched.Values.SelectMany(w => w.Series).Where(s => q.ItemIds.Contains(s.Id)).DistinctBy(s => s.Id).ToList();
        }

        Assert.Equal(_folderId, q.ParentId);
        var watched = _watched[q.User!.Id];
        return q.IncludeItemTypes.Single() switch
        {
            BaseItemKind.Movie when q.IsPlayed == true => watched.PlayedMovies,
            BaseItemKind.Movie when q.IsResumable == true => watched.ResumableMovies,
            BaseItemKind.Episode when q.IsPlayed == true => watched.PlayedEpisodes,
            _ => []
        };
    }

    private static Movie Movie(params string[] genres) => new() { Id = Guid.NewGuid(), Genres = genres, RunTimeTicks = 1000 };

    private void WatchSeries(User user, int episodes, params string[] genres)
    {
        var series = new Series { Id = Guid.NewGuid(), Genres = genres };
        _watched[user.Id].Series.Add(series);
        for (var i = 0; i < episodes; i++)
        {
            _watched[user.Id].PlayedEpisodes.Add(new Episode { Id = Guid.NewGuid(), SeriesId = series.Id });
        }
    }

    private void StopMovieAt(User user, double fraction, params string[] genres)
    {
        var movie = Movie(genres);
        _watched[user.Id].ResumableMovies.Add(movie);
        _watched[user.Id].Positions[movie.Id] = (long)(movie.RunTimeTicks!.Value * fraction);
    }

    [Fact]
    public void GetRanking_CountsHalfWatchedMoviesAndSeriesWithThreeEpisodes()
    {
        _watched[_viewer.Id].PlayedMovies.Add(Movie("Action", "Comedy"));
        StopMovieAt(_viewer, 0.6, "Action");
        StopMovieAt(_viewer, 0.3, "Romance");
        WatchSeries(_viewer, 3, "Thriller");
        WatchSeries(_viewer, 2, "Horror");

        var ranking = _service.GetRanking(_viewer.Id, "Shows");

        Assert.Equal(["Action", "Comedy", "Thriller"], ranking.Genres.Select(g => g.Name));
        Assert.Equal([2, 1, 1], ranking.Genres.Select(g => g.Titles));
    }

    [Fact]
    public void GetRanking_DiscoveryIsWhatOthersWatch_OutsideTheTopHalf()
    {
        _watched[_viewer.Id].PlayedMovies.Add(Movie("Action"));
        _watched[_viewer.Id].PlayedMovies.Add(Movie("Action"));
        _watched[_viewer.Id].PlayedMovies.Add(Movie("Drama"));
        _watched[_other.Id].PlayedMovies.Add(Movie("Action", "Drama"));
        WatchSeries(_other, 5, "Horror");

        var ranking = _service.GetRanking(_viewer.Id, "Shows");

        // Action is the viewer's top genre; Drama they rarely watch; Horror never.
        Assert.Equal(["Drama", "Horror"], ranking.Discovery.Select(g => g.Name).Order());
    }

    [Fact]
    public void GetRanking_UnknownLibrary_IsEmpty()
    {
        var ranking = _service.GetRanking(_viewer.Id, "Anime");

        Assert.Empty(ranking.Genres);
        Assert.Empty(ranking.Discovery);
    }

    private sealed class Watched
    {
        public List<BaseItem> PlayedMovies { get; } = [];

        public List<BaseItem> ResumableMovies { get; } = [];

        public List<BaseItem> PlayedEpisodes { get; } = [];

        public List<Series> Series { get; } = [];

        public Dictionary<Guid, long> Positions { get; } = [];
    }
}
