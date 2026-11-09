using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Emby.Server.Implementations.PirateFin.Library;
using Emby.Server.Implementations.PirateFin.Storage;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
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
/// YTS movies are matched to metadata by IMDb id; matching by name picked unrelated movies.
/// </summary>
public class MovieGeneratorTests
{
    private const string LibraryPath = "/media/movies";

    private readonly Mock<ILibraryManager> _libraryManager = new();
    private readonly Mock<IMetadataRefreshQueue> _refreshQueue = new();
    private readonly Dictionary<Guid, BaseItem> _items = [];
    private readonly Folder _libraryFolder = new() { Id = Guid.NewGuid(), Path = LibraryPath };
    private readonly MovieGenerator _generator;

    public MovieGeneratorTests()
    {
        _libraryManager.Setup(l => l.GetVirtualFolders())
            .Returns([new VirtualFolderInfo { Name = "Movies", Locations = [LibraryPath], ItemId = Guid.NewGuid().ToString() }]);
        _libraryManager.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.Path == LibraryPath && q.IsFolder == true)))
            .Returns([_libraryFolder]);
        _libraryManager.Setup(l => l.GetNewItemId(It.IsAny<string>(), It.IsAny<Type>()))
            .Returns((string path, Type type) => TestItemIds.For(path, type));
        _libraryManager.Setup(l => l.GetItemById(It.IsAny<Guid>()))
            .Returns((Guid id) => _items.GetValueOrDefault(id)!);
        _libraryManager.Setup(l => l.CreateItem(It.IsAny<BaseItem>(), It.IsAny<BaseItem>()))
            .Callback<BaseItem, BaseItem>((item, _) => _items[item.Id] = item);

        _generator = new MovieGenerator(_libraryManager.Object, _refreshQueue.Object, CreateStorage(_libraryManager.Object), NullLogger<MovieGenerator>.Instance);
    }

    private static ScheduleMovie LadyGaga() => new()
    {
        Title = "Apple Music Live: Lady Gaga Mayhem Requiem",
        Year = 2026,
        ImdbId = "tt9999999",
        Overview = "Concert film.",
        ImageUrl = "https://yts.example/large-cover.jpg",
        MagnetUrl = "magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567"
    };

    // Mirrors what the old name search did: wrong title, wrong ids, wrong artwork.
    private Movie AddMismatchedPlaceholder(ScheduleMovie movie)
    {
        var path = Path.Combine(LibraryPath, "Apple Music Live Lady Gaga Mayhem Requiem 2026.mkv");
        var existing = new Movie
        {
            Id = _libraryManager.Object.GetNewItemId(path, typeof(Movie)),
            ParentId = _libraryFolder.Id,
            Path = path,
            Name = "Pizza Movies",
            IsInternet = true,
            DownloadUrl = movie.MagnetUrl,
            ImageInfos = [new ItemImageInfo { Path = "wrong.jpg", Type = ImageType.Primary }]
        };
        existing.SetProviderId(MetadataProvider.Tmdb, "1436219");
        _items[existing.Id] = existing;
        return existing;
    }

    [Fact]
    public async Task NewMovie_GetsImdbIdForExactMetadataMatch()
    {
        await _generator.GenerateMoviesAsync([LadyGaga()], "Movies", CancellationToken.None);

        var movie = Assert.Single(_items.Values.OfType<Movie>());
        Assert.Equal("Apple Music Live: Lady Gaga Mayhem Requiem", movie.Name);
        Assert.Equal("tt9999999", movie.GetProviderId(MetadataProvider.Imdb));
        _refreshQueue.Verify(q => q.Enqueue(movie, It.IsAny<IReadOnlyList<BaseItem>>()), Times.Once);
    }

    [Fact]
    public async Task NewMovie_GetsItsOwnFolder()
    {
        await _generator.GenerateMoviesAsync([LadyGaga()], "Movies", CancellationToken.None);

        var movie = Assert.Single(_items.Values.OfType<Movie>());
        const string FileTitle = "Apple Music Live Lady Gaga Mayhem Requiem 2026";
        var path = Path.Combine(LibraryPath, FileTitle, FileTitle + ".mkv");
        Assert.Equal(path, movie.Path);
        Assert.Equal(_libraryManager.Object.GetNewItemId(path, typeof(Movie)), movie.Id);
        Assert.Equal(movie.Id, _generator.FindExistingMovieId(LadyGaga(), "Movies"));
    }

    [Fact]
    public async Task MismatchedPlaceholder_IsResetAndRematched()
    {
        var existing = AddMismatchedPlaceholder(LadyGaga());
        await _generator.GenerateMoviesAsync([LadyGaga()], "Movies", CancellationToken.None);

        Assert.Equal("Apple Music Live: Lady Gaga Mayhem Requiem", existing.Name);
        Assert.Equal("tt9999999", existing.GetProviderId(MetadataProvider.Imdb));
        Assert.Null(existing.GetProviderId(MetadataProvider.Tmdb));
        Assert.Empty(existing.ImageInfos);
        _refreshQueue.Verify(q => q.Enqueue(existing, It.IsAny<IReadOnlyList<BaseItem>>()), Times.Once);
        _libraryManager.Verify(l => l.UpdateItemAsync(existing, _libraryFolder, ItemUpdateType.MetadataEdit, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DownloadedMovie_IsNeverRematched()
    {
        var existing = AddMismatchedPlaceholder(LadyGaga());
        existing.IsInternet = false;
        existing.IsDownloaded = true;

        await _generator.GenerateMoviesAsync([LadyGaga()], "Movies", CancellationToken.None);

        Assert.Equal("Pizza Movies", existing.Name);
        _refreshQueue.Verify(q => q.Enqueue(It.IsAny<BaseItem>(), It.IsAny<IReadOnlyList<BaseItem>>()), Times.Never);
    }

    private static LibraryStorage CreateStorage(ILibraryManager libraryManager)
    {
        var options = new Mock<IOptionsMonitor<QBitTorrentOptions>>();
        options.Setup(o => o.CurrentValue).Returns(new QBitTorrentOptions());
        return new LibraryStorage(libraryManager, options.Object, NullLogger<LibraryStorage>.Instance);
    }
}
