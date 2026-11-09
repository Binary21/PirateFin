using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Emby.Server.Implementations.PirateFin.Trailers;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
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
/// Trailers saved next to downloaded titles: only downloaded titles with their own folder and a YouTube trailer, only
/// with room on the drive, never over an existing trailer; and (with PIRATEFIN_NETWORK_TESTS=1) a real download and
/// delete.
/// </summary>
public sealed class TrailerLibraryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "piratefin-trailer-library-" + Guid.NewGuid().ToString("N"));
    private readonly Mock<ILibraryStorage> _storage = new();
    private readonly Mock<IProviderManager> _providerManager = new();
    private readonly TrailerOptions _options = new();

    public TrailerLibraryTests()
    {
        Directory.CreateDirectory(_root);
        _storage.Setup(s => s.HasRoomAt(It.IsAny<string>())).Returns(true);
    }

    public void Dispose() => Directory.Delete(_root, true);

    private TrailerLibrary CreateLibrary()
    {
        var paths = Mock.Of<IApplicationPaths>(p => p.DataPath == Path.Combine(_root, "data"));
        var http = new Mock<IHttpClientFactory>();
        http.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient());
        var libraryManager = new Mock<ILibraryManager>();
        libraryManager.Setup(l => l.GetCollectionFolders(It.IsAny<BaseItem>())).Returns(new List<Folder>());
        libraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>())).Returns(new List<BaseItem>());

        return new TrailerLibrary(
            new YtDlp(http.Object, paths, NullLogger<YtDlp>.Instance),
            Mock.Of<IMediaEncoder>(e => e.EncoderPath == "ffmpeg"),
            libraryManager.Object,
            _providerManager.Object,
            Mock.Of<IFileSystem>(),
            _storage.Object,
            Mock.Of<IDownloadManager>(),
            Mock.Of<IOptionsMonitor<TrailerOptions>>(o => o.CurrentValue == _options),
            paths,
            NullLogger<TrailerLibrary>.Instance);
    }

    private Movie DownloadedMovie(string trailerUrl = "https://www.youtube.com/watch?v=rl_DJZCyEH4")
    {
        var folder = Path.Combine(_root, "Movies", "A Movie (2026)");
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "A Movie (2026).mkv");
        File.WriteAllText(file, "video");
        return new Movie { Id = Guid.NewGuid(), Name = "A Movie", Path = file, RemoteTrailers = [new MediaUrl { Url = trailerUrl }] };
    }

    [Theory]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ", "dQw4w9WgXcQ")]
    [InlineData("https://youtu.be/rl_DJZCyEH4", "rl_DJZCyEH4")]
    [InlineData("https://www.youtube.com/embed/N-J-HR3quc4?autoplay=1", "N-J-HR3quc4")]
    [InlineData("https://vimeo.com/123", null)]
    [InlineData(null, null)]
    public void GetYouTubeId_ReadsTrailerLinks(string? url, string? id)
    {
        Assert.Equal(id, TrailerLibrary.GetYouTubeId(url));
    }

    [Theory]
    [InlineData("ERROR: [youtube] abc: Requested format is not available.", true)]
    [InlineData("ERROR: [youtube] aaaaaaaaaaa: This video is unavailable", true)]
    [InlineData("ERROR: unable to download video data: HTTP Error 403: Forbidden", false)]
    public void IsPermanentFailure_SortsFailures(string output, bool permanent)
    {
        Assert.Equal(permanent, TrailerLibrary.IsPermanentFailure(output));
    }

    [Fact]
    public void GetTitleFolder_OnlyDownloadedMoviesInTheirOwnFolder()
    {
        using var library = CreateLibrary();
        var movie = DownloadedMovie();

        Assert.Equal(Path.GetDirectoryName(movie.Path), library.GetTitleFolder(movie));

        // A placeholder that hasn't downloaded yet.
        movie.IsInternet = true;
        movie.IsDownloaded = false;
        Assert.Null(library.GetTitleFolder(movie));

        movie.IsDownloaded = true;
        File.Delete(movie.Path!);
        Assert.Null(library.GetTitleFolder(movie));
    }

    [Fact]
    public async Task SaveAsync_SkipsWhenOffWithoutTrailerOrAlreadyHasOne()
    {
        using var library = CreateLibrary();

        _options.Enabled = false;
        Assert.Equal(TrailerSaveResult.Skipped, await library.SaveAsync(DownloadedMovie(), CancellationToken.None));
        _options.Enabled = true;

        Assert.Equal(TrailerSaveResult.Skipped, await library.SaveAsync(DownloadedMovie("https://vimeo.com/1"), CancellationToken.None));

        var movie = DownloadedMovie();
        var trailers = Path.Combine(Path.GetDirectoryName(movie.Path)!, TrailerLibrary.TrailersFolder);
        Directory.CreateDirectory(trailers);
        await File.WriteAllTextAsync(Path.Combine(trailers, "my own trailer.mkv"), "mine", TestContext.Current.CancellationToken);
        Assert.Equal(TrailerSaveResult.Skipped, await library.SaveAsync(movie, CancellationToken.None));
    }

    [Fact]
    public async Task SaveAsync_DriveBelowMinimumFreeSpace_DoesNotDownload()
    {
        using var library = CreateLibrary();
        _storage.Setup(s => s.HasRoomAt(It.IsAny<string>())).Returns(false);

        Assert.Equal(TrailerSaveResult.NoRoom, await library.SaveAsync(DownloadedMovie(), CancellationToken.None));
        Assert.False(Directory.Exists(Path.Combine(_root, "data", "piratefin", "tools")));
    }

    [Fact]
    public async Task SaveAsync_RealTrailer_SavedNextToTheMovie_ThenDeleted()
    {
        if (Environment.GetEnvironmentVariable("PIRATEFIN_NETWORK_TESTS") != "1")
        {
            return;
        }

        using var library = CreateLibrary();
        var movie = DownloadedMovie();

        Assert.Equal(TrailerSaveResult.Saved, await library.SaveAsync(movie, TestContext.Current.CancellationToken));
        var trailer = Path.Combine(Path.GetDirectoryName(movie.Path)!, TrailerLibrary.TrailersFolder, TrailerLibrary.TrailerFile);
        Assert.True(File.Exists(trailer));
        Assert.Equal(1, library.GetSummary().Count);
        _providerManager.Verify(p => p.QueueRefresh(movie.Id, It.IsAny<MetadataRefreshOptions>(), It.IsAny<RefreshPriority>()), Times.Once);

        // Already has one now.
        Assert.Equal(TrailerSaveResult.Skipped, await library.SaveAsync(movie, TestContext.Current.CancellationToken));

        // A removed video is remembered as unavailable.
        Assert.Equal(TrailerSaveResult.Unavailable, await library.SaveAsync(DownloadedMovieIn("Gone", "https://youtu.be/aaaaaaaaaaa"), TestContext.Current.CancellationToken));

        Assert.Equal(1, await library.DeleteAllAsync(TestContext.Current.CancellationToken));
        Assert.False(Directory.Exists(Path.GetDirectoryName(trailer)));
        Assert.Equal(0, library.GetSummary().Count);
    }

    private Movie DownloadedMovieIn(string name, string trailerUrl)
    {
        var folder = Path.Combine(_root, "Movies", name);
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, name + ".mkv");
        File.WriteAllText(file, "video");
        return new Movie { Id = Guid.NewGuid(), Name = name, Path = file, RemoteTrailers = [new MediaUrl { Url = trailerUrl }] };
    }
}
