using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Emby.Server.Implementations.PirateFin.Library;
using Emby.Server.Implementations.PirateFin.Storage;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.PirateFin;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.PirateFin;

/// <summary>
/// Downloads only go to drives with the minimum free space, and wait when none has it.
/// </summary>
public sealed class LibraryStorageTests : IDisposable
{
    private readonly string _libraryPath = Path.Combine(Path.GetTempPath(), "piratefin-storage-" + Guid.NewGuid().ToString("N"));
    private readonly Mock<ILibraryManager> _libraryManager = new();
    private readonly QBitTorrentOptions _options = new();
    private readonly LibraryStorage _storage;

    public LibraryStorageTests()
    {
        Directory.CreateDirectory(_libraryPath);
        var collectionFolder = new CollectionFolder { Name = "Shows", PhysicalLocationsList = [_libraryPath] };
        _libraryManager.Setup(l => l.GetCollectionFolders(It.IsAny<BaseItem>())).Returns(new List<Folder> { collectionFolder });

        var options = new Mock<IOptionsMonitor<QBitTorrentOptions>>();
        options.Setup(o => o.CurrentValue).Returns(_options);
        _storage = new LibraryStorage(_libraryManager.Object, options.Object, NullLogger<LibraryStorage>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_libraryPath))
        {
            Directory.Delete(_libraryPath, true);
        }
    }

    private Episode NewEpisode() => new()
    {
        Path = Path.Combine(_libraryPath, "My Show", "Season 1", "My Show - S01E01.mkv"),
        IsPlaceHolder = true
    };

    [Fact]
    public void DriveWithRoom_DownloadsInPlace()
    {
        _options.MinimumFreeSpaceGb = 0;
        var episode = NewEpisode();

        Assert.True(_storage.CanStore(episode));
        var folder = _storage.PrepareFolder(episode, _ => false, CancellationToken.None);

        Assert.Equal(Path.Combine(_libraryPath, "My Show", "Season 1"), folder);
        Assert.True(Directory.Exists(folder));
    }

    [Fact]
    public void NoDriveWithRoom_Waits()
    {
        // More than any drive has free: the library's only drive counts as full.
        _options.MinimumFreeSpaceGb = int.MaxValue;
        var episode = NewEpisode();

        Assert.False(_storage.CanStore(episode));
        Assert.False(_storage.CanStore(new Season { Path = Path.Combine(_libraryPath, "My Show", "Season 1") }));
        Assert.Null(_storage.PrepareFolder(episode, _ => false, CancellationToken.None));
        Assert.False(Directory.Exists(Path.Combine(_libraryPath, "My Show")));
    }

    [Fact]
    public void NewItems_GoToTheLibraryFolder()
    {
        var folder = new Folder { Id = Guid.NewGuid(), Path = _libraryPath };
        var library = new PirateFinLibrary("Shows", [new PirateFinLocation(_libraryPath, folder)], Guid.NewGuid());

        Assert.Equal(_libraryPath, _storage.ChooseNewItemLocation(library).Path);
    }
}
