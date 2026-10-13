using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.Library.Subscription;

/// <summary>
/// Pipeline stage 2: turning a fetched ScheduleEpisode into a placeholder (temporary) library item.
/// </summary>
public class EpisodeGeneratorTests
{
    private const string LibraryName = "Anime";
    private const string LibraryPath = "/media/anime";

    private readonly Mock<ILibraryManager> _libraryManager = new();
    private readonly Mock<IProviderManager> _providerManager = new();
    private readonly EpisodeGenerator _generator;

    public EpisodeGeneratorTests()
    {
        _generator = new EpisodeGenerator(
            NullLogger<EpisodeGenerator>.Instance,
            _libraryManager.Object,
            _providerManager.Object,
            Mock.Of<IFileSystem>());

        _providerManager
            .Setup(p => p.RefreshFullItem(It.IsAny<BaseItem>(), It.IsAny<MetadataRefreshOptions>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
    }

    private void SetUpLibrary(BaseItem parentFolder)
    {
        _libraryManager
            .Setup(l => l.GetVirtualFolders())
            .Returns(new List<VirtualFolderInfo> { new() { Name = LibraryName, Locations = new[] { LibraryPath } } });

        _libraryManager
            .Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.Path == LibraryPath && q.IsFolder == true)))
            .Returns(new List<BaseItem> { parentFolder });
    }

    [Fact]
    public async Task GenerateEpisode_NoMatchingLibrary_DoesNotCreateAnyItems()
    {
        _libraryManager.Setup(l => l.GetVirtualFolders()).Returns(new List<VirtualFolderInfo>());
        var scheduleEpisode = new ScheduleEpisode { Title = "Show", Season = 1, Number = 1 };

        await _generator.GenerateEpisode(new List<ScheduleEpisode> { scheduleEpisode }, LibraryName, DateTime.Today);

        _libraryManager.Verify(l => l.CreateItem(It.IsAny<BaseItem>(), It.IsAny<BaseItem>()), Times.Never);
    }

    [Fact]
    public async Task GenerateEpisode_LibraryPathNotConfigured_DoesNotCreateAnyItems()
    {
        _libraryManager
            .Setup(l => l.GetVirtualFolders())
            .Returns(new List<VirtualFolderInfo> { new() { Name = LibraryName, Locations = Array.Empty<string>() } });
        var scheduleEpisode = new ScheduleEpisode { Title = "Show", Season = 1, Number = 1 };

        await _generator.GenerateEpisode(new List<ScheduleEpisode> { scheduleEpisode }, LibraryName, DateTime.Today);

        _libraryManager.Verify(l => l.CreateItem(It.IsAny<BaseItem>(), It.IsAny<BaseItem>()), Times.Never);
    }

    [Fact]
    public async Task GenerateEpisode_NoLibraryParentItemResolved_DoesNotCreateAnyItems()
    {
        _libraryManager
            .Setup(l => l.GetVirtualFolders())
            .Returns(new List<VirtualFolderInfo> { new() { Name = LibraryName, Locations = new[] { LibraryPath } } });
        _libraryManager
            .Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem>());
        var scheduleEpisode = new ScheduleEpisode { Title = "Show", Season = 1, Number = 1 };

        await _generator.GenerateEpisode(new List<ScheduleEpisode> { scheduleEpisode }, LibraryName, DateTime.Today);

        _libraryManager.Verify(l => l.CreateItem(It.IsAny<BaseItem>(), It.IsAny<BaseItem>()), Times.Never);
    }

    [Fact]
    public async Task GenerateEpisode_HappyPath_CreatesPlaceholderSeriesSeasonAndEpisode()
    {
        var parentFolder = new Folder { Id = Guid.NewGuid(), Path = LibraryPath };
        SetUpLibrary(parentFolder);

        var seriesId = Guid.NewGuid();
        var seasonId = Guid.NewGuid();
        var episodeId = Guid.NewGuid();
        _libraryManager.Setup(l => l.GetNewItemId(It.IsAny<string>(), typeof(Series))).Returns(seriesId);
        _libraryManager.Setup(l => l.GetNewItemId(It.IsAny<string>(), typeof(Season))).Returns(seasonId);
        _libraryManager.Setup(l => l.GetNewItemId(It.IsAny<string>(), typeof(Episode))).Returns(episodeId);
        _libraryManager.Setup(l => l.GetItemById(It.IsAny<Guid>())).Returns((BaseItem)null!);

        Episode? createdEpisode = null;
        _libraryManager
            .Setup(l => l.CreateItem(It.IsAny<BaseItem>(), It.IsAny<BaseItem>()))
            .Callback<BaseItem, BaseItem>((item, _) =>
            {
                if (item is Episode episode)
                {
                    createdEpisode = episode;
                }
            });

        var startOfWeek = new DateTime(2026, 9, 28);
        var scheduleEpisode = new ScheduleEpisode
        {
            Title = "My Show",
            AlternateTitle = "Alt Title",
            Season = 1,
            Number = 3,
            FirstAiring = startOfWeek.AddDays(2),
            MagnetUrl = "magnet:?xt=urn:btih:ABC123",
            ImageUrl = "http://example/image.jpg"
        };

        await _generator.GenerateEpisode(new List<ScheduleEpisode> { scheduleEpisode }, LibraryName, startOfWeek);

        _libraryManager.Verify(l => l.CreateItem(It.IsAny<Series>(), parentFolder), Times.Once);
        _libraryManager.Verify(l => l.CreateItem(It.IsAny<Season>(), It.IsAny<BaseItem>()), Times.Once);
        _libraryManager.Verify(l => l.CreateItem(It.IsAny<Episode>(), It.IsAny<BaseItem>()), Times.Once);

        Assert.NotNull(createdEpisode);
        Assert.True(createdEpisode!.IsPlaceHolder);
        Assert.True(createdEpisode.IsVirtualItem);
        Assert.True(createdEpisode.IsInternet);
        Assert.True(createdEpisode.Scheduled);
        Assert.False(createdEpisode.IsDownloaded);
        Assert.True(createdEpisode.IsDownloadable);
        Assert.Equal("magnet:?xt=urn:btih:ABC123", createdEpisode.DownloadUrl);
        Assert.Equal(seriesId, createdEpisode.SeriesId);
        Assert.Equal(seasonId, createdEpisode.SeasonId);
        Assert.Equal(seasonId, createdEpisode.ParentId);
        Assert.Equal(startOfWeek, createdEpisode.EpisodeReleaseWeek);
        Assert.Equal(LibraryName, createdEpisode.LibraryType);
        Assert.Equal(scheduleEpisode.FirstAiring, createdEpisode.FirstAiring);
        Assert.Equal("Alt Title", createdEpisode.AlternateTitle);
        Assert.Equal(3, createdEpisode.IndexNumber);
        Assert.Equal(1, createdEpisode.ParentIndexNumber);

        var expectedPath = Path.Combine(LibraryPath, "My Show", "Season 1", "My Show - S01E03.mkv");
        Assert.Equal(expectedPath, createdEpisode.Path);
    }

    [Fact]
    public async Task GenerateEpisode_EpisodeAlreadyExists_DoesNotCreateDuplicateOrOverwriteRealEpisode()
    {
        var parentFolder = new Folder { Id = Guid.NewGuid(), Path = LibraryPath };
        SetUpLibrary(parentFolder);

        var seriesId = Guid.NewGuid();
        var seasonId = Guid.NewGuid();
        var episodeId = Guid.NewGuid();
        _libraryManager.Setup(l => l.GetNewItemId(It.IsAny<string>(), typeof(Series))).Returns(seriesId);
        _libraryManager.Setup(l => l.GetNewItemId(It.IsAny<string>(), typeof(Season))).Returns(seasonId);
        _libraryManager.Setup(l => l.GetNewItemId(It.IsAny<string>(), typeof(Episode))).Returns(episodeId);

        var existingSeries = new Series { Id = seriesId, ParentId = parentFolder.Id };
        var existingSeason = new Season { Id = seasonId, ParentId = seriesId };

        // Simulates the download pipeline having already replaced the placeholder with a real, downloaded episode.
        var downloadedEpisode = new Episode { Id = episodeId, IsDownloaded = true, IsPlaceHolder = false };

        _libraryManager.Setup(l => l.GetItemById(seriesId)).Returns(existingSeries);
        _libraryManager.Setup(l => l.GetItemById(seasonId)).Returns(existingSeason);
        _libraryManager.Setup(l => l.GetItemById(episodeId)).Returns(downloadedEpisode);

        var scheduleEpisode = new ScheduleEpisode { Title = "My Show", Season = 1, Number = 3 };

        await _generator.GenerateEpisode(new List<ScheduleEpisode> { scheduleEpisode }, LibraryName, DateTime.Today);

        _libraryManager.Verify(l => l.CreateItem(It.IsAny<BaseItem>(), It.IsAny<BaseItem>()), Times.Never);
    }
}
