using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Emby.Server.Implementations.PirateFin.Library;
using Jellyfin.Data.Enums;
using Jellyfin.Extensions;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.PirateFin;

/// <summary>
/// Seasons sharing a number are merged into the one with the folder; the other's episodes move over.
/// </summary>
public sealed class DuplicateSeasonMergerTests : IDisposable
{
    private readonly Mock<ILibraryManager> _libraryManager = new();
    private readonly List<BaseItem> _items = [];
    private readonly Guid _seriesId = Guid.NewGuid();
    private readonly DuplicateSeasonMerger _merger;

    public DuplicateSeasonMergerTests()
    {
        _libraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>())).Returns((InternalItemsQuery q) => Query(q));
        _libraryManager.Setup(l => l.DeleteItem(It.IsAny<BaseItem>(), It.IsAny<DeleteOptions>(), false))
            .Callback((BaseItem item, DeleteOptions _, bool _) => _items.Remove(item));
        _libraryManager.Setup(l => l.GetItemById(_seriesId)).Returns(new Series { Id = _seriesId });
        _merger = new DuplicateSeasonMerger(_libraryManager.Object, TimeProvider.System, NullLogger<DuplicateSeasonMerger>.Instance);
    }

    public void Dispose() => _merger.Dispose();

    private List<BaseItem> Query(InternalItemsQuery q)
    {
        IEnumerable<BaseItem> items = _items;
        if (q.IncludeItemTypes.Contains(BaseItemKind.Season))
        {
            items = items.OfType<Season>();
        }

        if (q.IncludeItemTypes.Contains(BaseItemKind.Episode))
        {
            items = items.OfType<Episode>();
        }

        if (!q.ParentId.IsEmpty())
        {
            items = items.Where(i => i.ParentId.Equals(q.ParentId));
        }

        if (q.AncestorIds.Length > 0)
        {
            items = items.OfType<Episode>().Where(e => q.AncestorIds.Contains(e.SeasonId));
        }

        return items.ToList();
    }

    private Season AddSeason(int number, string? path, bool subscribed = false)
    {
        var season = new Season { Id = Guid.NewGuid(), IndexNumber = number, Path = path, Name = "Season " + number, SeriesId = _seriesId, ParentId = _seriesId, Subscribed = subscribed };
        _items.Add(season);
        return season;
    }

    private Episode AddEpisode(Season season, int number, bool placeholder = true)
    {
        var episode = new Episode { Id = Guid.NewGuid(), IndexNumber = number, ParentId = season.Id, SeasonId = season.Id, IsPlaceHolder = placeholder, IsDownloaded = !placeholder };
        _items.Add(episode);
        return episode;
    }

    [Fact]
    public async Task MergesIntoTheSeasonWithTheFolder()
    {
        var folderless = AddSeason(2, null, subscribed: true);
        var folder = AddSeason(2, @"C:\Shows\9-1-1\Season 2");
        var other = AddSeason(3, null);
        var moved = AddEpisode(folderless, 1);
        var copy = AddEpisode(folderless, 2);
        AddEpisode(folder, 2);

        var merged = await _merger.MergeAllAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, merged);
        Assert.DoesNotContain(folderless, _items);
        Assert.DoesNotContain(copy, _items);
        Assert.Contains(other, _items);
        Assert.Equal(folder.Id, moved.ParentId);
        Assert.Equal(folder.Id, moved.SeasonId);
        Assert.True(folder.Subscribed);
        _libraryManager.Verify(l => l.UpdateItemAsync(moved, folder, ItemUpdateType.MetadataEdit, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task LeavesTwoFoldersAlone()
    {
        var first = AddSeason(1, @"C:\Shows\X\Season 1");
        var second = AddSeason(1, @"C:\Shows\X\Season 01");

        var merged = await _merger.MergeSeriesAsync(_seriesId, TestContext.Current.CancellationToken);

        Assert.Equal(0, merged);
        Assert.Contains(first, _items);
        Assert.Contains(second, _items);
    }
}
