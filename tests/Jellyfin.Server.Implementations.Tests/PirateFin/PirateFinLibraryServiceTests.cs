using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Emby.Server.Implementations.PirateFin.Library;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.PirateFin;
using MediaBrowser.Controller.PirateFin.Models;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.PirateFin;

/// <summary>
/// The dashboard's "refresh latest" reset: placeholders go, anything downloaded or downloading stays.
/// </summary>
public sealed class PirateFinLibraryServiceTests : IDisposable
{
    private readonly string _libraryPath = Path.Combine(Path.GetTempPath(), "piratefin-reset-" + Guid.NewGuid().ToString("N"));
    private readonly Guid _collectionFolderId = Guid.NewGuid();
    private readonly Mock<ILibraryManager> _libraryManager = new();
    private readonly Mock<IInternetScheduleFetcher> _fetcher = new();
    private readonly Mock<IEpisodeGenerator> _episodeGenerator = new();
    private readonly Mock<IMovieGenerator> _movieGenerator = new();
    private readonly List<BaseItem> _items = [];
    private readonly List<BaseItem> _deleted = [];
    private readonly PirateFinLibraryService _service;

    public PirateFinLibraryServiceTests()
    {
        var folder = new Folder { Id = Guid.NewGuid(), Path = _libraryPath };
        _libraryManager.Setup(l => l.GetVirtualFolders()).Returns(
        [
            new VirtualFolderInfo { Name = "Shows", Locations = [_libraryPath], ItemId = _collectionFolderId.ToString() },
            new VirtualFolderInfo { Name = "Movies", Locations = [_libraryPath], ItemId = _collectionFolderId.ToString() }
        ]);
        _libraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>())).Returns((InternalItemsQuery q) => Find(q, folder));
        _libraryManager.Setup(l => l.DeleteItem(It.IsAny<BaseItem>(), It.IsAny<DeleteOptions>(), It.IsAny<bool>()))
            .Callback<BaseItem, DeleteOptions, bool>((item, _, _) =>
            {
                _items.Remove(item);
                _deleted.Add(item);
            });

        _service = new PirateFinLibraryService(
            _libraryManager.Object,
            _fetcher.Object,
            _episodeGenerator.Object,
            _movieGenerator.Object,
            NullLogger<PirateFinLibraryService>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_libraryPath))
        {
            Directory.Delete(_libraryPath, true);
        }
    }

    private List<BaseItem> Find(InternalItemsQuery query, Folder libraryFolder)
    {
        if (query.Path == _libraryPath)
        {
            return [libraryFolder];
        }

        if (query.Recursive)
        {
            return _items
                .Where(i => query.IncludeItemTypes.Contains(i.GetBaseItemKind()) && (!query.IsInternet.HasValue || i.IsInternet == query.IsInternet))
                .ToList();
        }

        return _items.Where(i => i.ParentId.Equals(query.ParentId)).ToList();
    }

    private T Add<T>(T item, BaseItem? parent = null)
        where T : BaseItem
    {
        item.Id = Guid.NewGuid();
        item.ParentId = parent?.Id ?? Guid.Empty;
        _items.Add(item);
        return item;
    }

    [Fact]
    public void RemovePlaceholders_Shows_KeepsDownloadedAndDownloading_RemovesEmptiedParents()
    {
        var keptSeries = Add(new Series { IsInternet = true });
        var keptSeason = Add(new Season { IsInternet = true }, keptSeries);
        var downloaded = Add(new Episode { IsInternet = false, IsDownloaded = true }, keptSeason);
        Add(new Episode { IsInternet = true, IsDownloaded = false }, keptSeason);

        var emptiedSeason = Add(new Season { IsInternet = true }, keptSeries);
        var downloading = Add(new Episode { IsInternet = true, IsDownloaded = false, DownloadStartedUtc = DateTime.UtcNow }, emptiedSeason);
        var otherSeason = Add(new Season { IsInternet = true }, keptSeries);
        Add(new Episode { IsInternet = true, IsDownloaded = false }, otherSeason);

        var goneSeries = Add(new Series { IsInternet = true });
        var goneSeason = Add(new Season { IsInternet = true }, goneSeries);
        Add(new Episode { IsInternet = true, IsDownloaded = false }, goneSeason);

        var userSeries = Add(new Series { IsInternet = false });

        var result = _service.RemovePlaceholders("shows");

        Assert.Equal(3, result.Episodes);
        Assert.Equal(2, result.Seasons);
        Assert.Equal(1, result.Series);
        Assert.Contains(downloaded, _items);
        Assert.Contains(downloading, _items);
        Assert.Contains(keptSeason, _items);
        Assert.Contains(emptiedSeason, _items);
        Assert.Contains(keptSeries, _items);
        Assert.Contains(userSeries, _items);
        Assert.Contains(otherSeason, _deleted);
        Assert.Contains(goneSeries, _deleted);
    }

    [Fact]
    public void RemovePlaceholders_Movies_KeepsDownloadedAndDownloading()
    {
        var downloading = Add(new Movie { IsInternet = true, IsDownloaded = false, DownloadStartedUtc = DateTime.UtcNow });
        var placeholder = Add(new Movie { IsInternet = true, IsDownloaded = false });
        var downloaded = Add(new Movie { IsInternet = false, IsDownloaded = true });

        var result = _service.RemovePlaceholders("Movies");

        Assert.Equal(1, result.Movies);
        Assert.Contains(placeholder, _deleted);
        Assert.Contains(downloading, _items);
        Assert.Contains(downloaded, _items);
    }

    [Fact]
    public async Task StartReset_RefreshesInBackground_AndRejectsConcurrentReset()
    {
        var release = new TaskCompletionSource();
        var refreshed = new TaskCompletionSource();
        _fetcher.Setup(f => f.GetLatestMoviesAsync(It.IsAny<CancellationToken>())).Returns(async () =>
        {
            await release.Task;
            return [];
        });
        _movieGenerator.Setup(g => g.GenerateMoviesAsync(It.IsAny<IReadOnlyList<ScheduleMovie>>(), "Movies", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask)
            .Callback(() => refreshed.TrySetResult());

        Assert.NotNull(_service.StartReset("Movies"));
        Assert.Null(_service.StartReset("Movies"));

        release.SetResult();
        await refreshed.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task RefreshAsync_UsesTheLibrarysSource()
    {
        IReadOnlyList<ScheduleEpisode> shows = [new() { Title = "Show" }];
        _fetcher.Setup(f => f.GetTvWeeklyScheduleAsync(It.IsAny<CancellationToken>())).ReturnsAsync(shows);

        await _service.RefreshAsync("Shows", CancellationToken.None);

        _episodeGenerator.Verify(g => g.GenerateEpisodesAsync(shows, "Shows", It.IsAny<CancellationToken>()), Times.Once);
        _fetcher.Verify(f => f.GetAnimeWeeklyScheduleAsync(It.IsAny<CancellationToken>()), Times.Never);
    }
}
