using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Emby.Server.Implementations.PirateFin.Library;
using Emby.Server.Implementations.PirateFin.Storage;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.PirateFin;
using MediaBrowser.Controller.PirateFin.Models;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.PirateFin;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.PirateFin;

/// <summary>
/// Weekly schedule -> placeholder series / season / episode, with metadata deferred to the refresh queue.
/// </summary>
public sealed class EpisodeGeneratorTests : IDisposable
{
    private const string LibraryName = "Anime";

    private readonly string _libraryPath = Path.Combine(Path.GetTempPath(), "piratefin-tests-" + Guid.NewGuid().ToString("N"));
    private readonly Mock<ILibraryManager> _libraryManager = new();
    private readonly Mock<IMetadataRefreshQueue> _refreshQueue = new();
    private readonly Dictionary<Guid, BaseItem> _items = [];
    private readonly Folder _libraryFolder;
    private readonly EpisodeGenerator _generator;

    public EpisodeGeneratorTests()
    {
        _libraryFolder = new Folder { Id = Guid.NewGuid(), Path = _libraryPath };
        _libraryManager.Setup(l => l.GetVirtualFolders())
            .Returns([new VirtualFolderInfo { Name = LibraryName, Locations = [_libraryPath], ItemId = Guid.NewGuid().ToString() }]);
        // Other queries (e.g. a season by number) find nothing.
        _libraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns([]);
        _libraryManager.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.Path == _libraryPath && q.IsFolder == true)))
            .Returns([_libraryFolder]);
        _libraryManager.Setup(l => l.GetNewItemId(It.IsAny<string>(), It.IsAny<Type>()))
            .Returns((string path, Type type) => TestItemIds.For(path, type));
        _libraryManager.Setup(l => l.GetItemById(It.IsAny<Guid>()))
            .Returns((Guid id) => _items.GetValueOrDefault(id)!);
        _libraryManager.Setup(l => l.CreateItem(It.IsAny<BaseItem>(), It.IsAny<BaseItem>()))
            .Callback<BaseItem, BaseItem>((item, _) => _items[item.Id] = item);
        _libraryManager.Setup(l => l.CreateItems(It.IsAny<IReadOnlyList<BaseItem>>(), It.IsAny<BaseItem>(), It.IsAny<CancellationToken>()))
            .Callback<IReadOnlyList<BaseItem>, BaseItem, CancellationToken>((items, _, _) =>
            {
                foreach (var item in items)
                {
                    _items[item.Id] = item;
                }
            });

        var writer = new SeriesPlaceholderWriter(_libraryManager.Object, _refreshQueue.Object, CreateStorage(_libraryManager.Object), NullLogger<SeriesPlaceholderWriter>.Instance);
        _generator = new EpisodeGenerator(_libraryManager.Object, writer, NullLogger<EpisodeGenerator>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_libraryPath))
        {
            Directory.Delete(_libraryPath, true);
        }
    }

    private void VerifyNothingCreated()
    {
        _libraryManager.Verify(l => l.CreateItem(It.IsAny<BaseItem>(), It.IsAny<BaseItem>()), Times.Never);
        _libraryManager.Verify(l => l.CreateItems(It.IsAny<IReadOnlyList<BaseItem>>(), It.IsAny<BaseItem>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private static ScheduleEpisode Scheduled(int number, DateTime firstAiring) => new()
    {
        Title = "My Show",
        AlternateTitle = "Boku no Show",
        Season = 1,
        Number = number,
        FirstAiring = firstAiring,
        ImageUrl = "http://example/poster.jpg"
    };

    [Fact]
    public async Task MissingLibrary_CreatesNothing()
    {
        _libraryManager.Setup(l => l.GetVirtualFolders()).Returns([]);

        await _generator.GenerateEpisodesAsync([Scheduled(1, DateTime.UtcNow)], LibraryName, CancellationToken.None);

        VerifyNothingCreated();
    }

    [Fact]
    public async Task NewShow_CreatesPlaceholderTree_AndQueuesMetadata()
    {
        var airs = new DateTime(2026, 10, 23, 15, 30, 0, DateTimeKind.Utc);

        await _generator.GenerateEpisodesAsync([Scheduled(3, airs), Scheduled(4, airs.AddDays(7))], LibraryName, CancellationToken.None);

        var series = Assert.Single(_items.Values.OfType<Series>());
        var season = Assert.Single(_items.Values.OfType<Season>());
        var episodes = _items.Values.OfType<Episode>().OrderBy(e => e.IndexNumber).ToList();
        Assert.Equal(2, episodes.Count);
        Assert.True(Directory.Exists(season.Path));

        var episode = episodes[0];
        Assert.Equal(Path.Combine(_libraryPath, "My Show", "Season 1", "My Show - S01E03.mkv"), episode.Path);
        Assert.Equal(airs, episode.FirstAiring);
        Assert.Equal(DateTimeKind.Utc, episode.FirstAiring!.Value.Kind);
        Assert.Equal("Episode 3", episode.Name);
        Assert.Equal("My Show", episode.SeriesName);
        Assert.Equal("Boku no Show", episode.AlternateTitle);
        Assert.Equal(LibraryName, episode.LibraryType);
        Assert.Equal(season.Id, episode.ParentId);
        Assert.Equal(series.Id, episode.SeriesId);
        Assert.True(episode.IsPlaceHolder && episode.IsVirtualItem && episode.IsInternet && episode.Scheduled);
        Assert.False(episode.IsDownloaded);
        Assert.False(episode.IsMissingEpisode);

        // No metadata provider call during generation: one background job, series first, then the episodes.
        _refreshQueue.Verify(q => q.Enqueue(series, It.Is<IReadOnlyList<BaseItem>>(c => c.Count == 2 && c.All(i => i is Episode))), Times.Once);
    }

    [Fact]
    public async Task ExistingPlaceholder_GetsCorrectedAirTime_WithoutDuplicate()
    {
        var wrong = new DateTime(2026, 10, 23, 11, 30, 0, DateTimeKind.Utc);
        await _generator.GenerateEpisodesAsync([Scheduled(3, wrong)], LibraryName, CancellationToken.None);
        var episode = _items.Values.OfType<Episode>().Single();
        _libraryManager.Invocations.Clear();

        var corrected = wrong.AddHours(4);
        await _generator.GenerateEpisodesAsync([Scheduled(3, corrected)], LibraryName, CancellationToken.None);

        Assert.Equal(corrected, episode.FirstAiring);
        VerifyNothingCreated();
        _libraryManager.Verify(l => l.UpdateItemAsync(episode, It.IsAny<BaseItem>(), ItemUpdateType.MetadataEdit, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DownloadedEpisode_IsNeverTouched()
    {
        await _generator.GenerateEpisodesAsync([Scheduled(3, DateTime.UtcNow)], LibraryName, CancellationToken.None);
        var episode = _items.Values.OfType<Episode>().Single();
        episode.IsDownloaded = true;
        episode.IsInternet = false;
        var airs = episode.FirstAiring;
        _libraryManager.Invocations.Clear();

        await _generator.GenerateEpisodesAsync([Scheduled(3, DateTime.UtcNow.AddDays(1))], LibraryName, CancellationToken.None);

        Assert.Equal(airs, episode.FirstAiring);
        VerifyNothingCreated();
        _libraryManager.Verify(l => l.UpdateItemAsync(It.IsAny<BaseItem>(), It.IsAny<BaseItem>(), It.IsAny<ItemUpdateType>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task NotYetReleasedAnime_IsSkipped()
    {
        var episode = Scheduled(1, DateTime.UtcNow.AddDays(1));
        episode.AiringStatus = "NOT_YET_RELEASED";

        await _generator.GenerateEpisodesAsync([episode], LibraryName, CancellationToken.None);

        Assert.Empty(_items);
    }

    private static LibraryStorage CreateStorage(ILibraryManager libraryManager)
    {
        var options = new Mock<IOptionsMonitor<QBitTorrentOptions>>();
        options.Setup(o => o.CurrentValue).Returns(new QBitTorrentOptions());
        return new LibraryStorage(libraryManager, options.Object, NullLogger<LibraryStorage>.Instance);
    }
}
