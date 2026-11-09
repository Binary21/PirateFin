using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Emby.Server.Implementations.PirateFin.Library;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.PirateFin;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.PirateFin;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.PirateFin;

/// <summary>
/// Filling in the libraries: every series gets its missing episodes looked up and a metadata refresh; the night run
/// covers series added in the last 7 days and series still airing.
/// </summary>
public sealed class LibraryPopulatorTests : IDisposable
{
    private readonly Mock<ILibraryManager> _libraryManager = new();
    private readonly Mock<IInternetSearchService> _searchService = new();
    private readonly Mock<IProviderManager> _providerManager = new();
    private readonly Guid _showsId = Guid.NewGuid();
    private readonly LibraryPopulator _populator;

    public LibraryPopulatorTests()
    {
        _libraryManager.Setup(l => l.GetVirtualFolders()).Returns([new VirtualFolderInfo { Name = "Shows", ItemId = _showsId.ToString() }]);
        _libraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>())).Returns([]);

        _populator = new LibraryPopulator(
            _libraryManager.Object,
            _searchService.Object,
            _providerManager.Object,
            Mock.Of<IFileSystem>(),
            Mock.Of<IOptionsMonitor<QBitTorrentOptions>>(o => o.CurrentValue == new QBitTorrentOptions()),
            TimeProvider.System,
            NullLogger<LibraryPopulator>.Instance);
    }

    public void Dispose() => _populator.Dispose();

    private void InShows(params Series[] series)
        => _libraryManager.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.ParentId.Equals(_showsId))))
            .Returns(new List<BaseItem>(series));

    [Fact]
    public async Task Run_FillsInEverySeries_AndCountsTheOnesNotFound()
    {
        var found = new Series { Id = Guid.NewGuid(), Name = "Found" };
        var missing = new Series { Id = Guid.NewGuid(), Name = "Missing" };
        _searchService.Setup(s => s.RefreshSeriesAsync(found, It.IsAny<CancellationToken>())).ReturnsAsync((2, 10));
        _searchService.Setup(s => s.RefreshSeriesAsync(missing, It.IsAny<CancellationToken>())).ReturnsAsync(((int, int)?)null);

        await _populator.RunAsync([found, missing], "Full", TimeSpan.Zero, TestContext.Current.CancellationToken);

        var status = _populator.GetStatus();
        Assert.False(status.Running);
        Assert.Equal(2, status.Total);
        Assert.Equal(2, status.Done);
        Assert.Equal(1, status.Failed);
        _providerManager.Verify(p => p.QueueRefresh(It.IsAny<Guid>(), It.IsAny<MetadataRefreshOptions>(), RefreshPriority.Low), Times.Exactly(2));
    }

    [Fact]
    public void Nightly_CoversRecentAndAiringSeries()
    {
        var recent = new Series { Id = Guid.NewGuid(), Name = "Recent", DateCreated = DateTime.UtcNow.AddDays(-2) };
        var airing = new Series { Id = Guid.NewGuid(), Name = "Airing", DateCreated = DateTime.UtcNow.AddYears(-1), Status = SeriesStatus.Continuing };
        var old = new Series { Id = Guid.NewGuid(), Name = "Old", DateCreated = DateTime.UtcNow.AddYears(-1), Status = SeriesStatus.Ended };
        InShows(recent, airing, old);

        var nightly = _populator.GetNightlySeries();

        Assert.Equal(["Airing", "Recent"], nightly.Select(s => s.Name).Order());
        Assert.Equal(3, _populator.GetAllSeries().Count);
    }
}
