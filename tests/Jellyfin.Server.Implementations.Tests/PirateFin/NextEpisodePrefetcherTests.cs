using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Emby.Server.Implementations.PirateFin.Downloads;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.PirateFin;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.PirateFin;

/// <summary>
/// Half way through an episode the viewer is subscribed to its season and the next two episodes download, stopping at
/// the first one that can't be downloaded.
/// </summary>
public sealed class NextEpisodePrefetcherTests
{
    private const long RunTime = 1000;

    private readonly Mock<ISessionManager> _sessionManager = new();
    private readonly Mock<ILibraryManager> _libraryManager = new();
    private readonly Mock<IDownloadManager> _downloadManager = new();
    private readonly Mock<ISeasonSubscriptions> _subscriptions = new();
    private readonly Mock<IInternetSearchService> _searchService = new();
    private readonly Series _series = new() { Id = Guid.NewGuid(), Name = "My Show" };
    private readonly Season _season = new() { Id = Guid.NewGuid(), Name = "Season 1" };
    private readonly User _user = new("viewer", "default", "default");

    public NextEpisodePrefetcherTests()
    {
        _libraryManager.Setup(l => l.GetItemById(_season.Id)).Returns(_season);
        _libraryManager.Setup(l => l.GetItemById(_series.Id)).Returns(_series);
        _downloadManager.Setup(d => d.QueueDownload(It.IsAny<BaseItem>())).Returns(true);

        _searchService.Setup(s => s.RefreshSeriesAsync(_series, It.IsAny<CancellationToken>()))
            .ReturnsAsync(((int Seasons, int Episodes)?)null);
        _subscriptions.Setup(s => s.SubscribeAsync(_season, _user.Id, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var prefetcher = new NextEpisodePrefetcher(
            _sessionManager.Object,
            _libraryManager.Object,
            _downloadManager.Object,
            _subscriptions.Object,
            _searchService.Object,
            TimeProvider.System,
            NullLogger<NextEpisodePrefetcher>.Instance);
        prefetcher.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
    }

    private Episode Episode(int number, string downloadUrl = "", bool downloaded = false) => new()
    {
        Id = Guid.NewGuid(),
        Name = "Episode " + number,
        IndexNumber = number,
        ParentIndexNumber = 1,
        SeriesId = _series.Id,
        SeasonId = _season.Id,
        RunTimeTicks = RunTime,
        DownloadUrl = downloadUrl,
        IsDownloaded = downloaded
    };

    private async Task WatchHalfAsync(Episode watching, params Episode[] series)
    {
        _libraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>())).Returns([watching, .. series]);
        _downloadManager.Setup(d => d.QueueDownload(It.IsAny<BaseItem>())).Returns(true);

        _sessionManager.Raise(s => s.PlaybackProgress += null, new PlaybackProgressEventArgs
        {
            Item = watching,
            PlaybackPositionTicks = RunTime / 2,
            Users = new List<User> { _user }
        });

        // The work runs in the background; give it a moment after the (instant) refresh.
        await Task.Delay(200);
    }

    [Fact]
    public async Task NextTwoAvailable_BothDownload_AndViewerIsSubscribed()
    {
        var next = Episode(8, "magnet:8");
        var after = Episode(9, "magnet:9");
        await WatchHalfAsync(Episode(7), next, after, Episode(10, "magnet:10"));

        _subscriptions.Verify(s => s.SubscribeAsync(_season, _user.Id, It.IsAny<CancellationToken>()), Times.Once);
        _downloadManager.Verify(d => d.QueueDownload(next), Times.Once);
        _downloadManager.Verify(d => d.QueueDownload(after), Times.Once);
        _downloadManager.Verify(d => d.QueueDownload(It.IsAny<BaseItem>()), Times.Exactly(2));
    }

    [Fact]
    public async Task NextNotAvailable_NothingDownloads()
    {
        await WatchHalfAsync(Episode(7), Episode(8), Episode(9, "magnet:9"));

        _downloadManager.Verify(d => d.QueueDownload(It.IsAny<BaseItem>()), Times.Never);
    }

    [Fact]
    public async Task NextAlreadyDownloaded_TheOneAfterDownloads()
    {
        var after = Episode(9, "magnet:9");
        await WatchHalfAsync(Episode(7), Episode(8, "magnet:8", downloaded: true), after);

        _downloadManager.Verify(d => d.QueueDownload(after), Times.Once);
        _downloadManager.Verify(d => d.QueueDownload(It.IsAny<BaseItem>()), Times.Once);
    }

    [Fact]
    public async Task ViewerWhoUnsubscribed_IsNotSubscribedAgain()
    {
        _subscriptions.Setup(s => s.HasUnsubscribed(_season.Id, _user.Id)).Returns(true);

        await WatchHalfAsync(Episode(7), Episode(8, "magnet:8"));

        _subscriptions.Verify(s => s.SubscribeAsync(It.IsAny<Season>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
