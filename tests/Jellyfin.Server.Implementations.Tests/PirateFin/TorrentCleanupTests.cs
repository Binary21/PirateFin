using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Emby.Server.Implementations.PirateFin.Downloads;
using Emby.Server.Implementations.PirateFin.Storage;
using Emby.Server.Implementations.PirateFin.Torrents;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
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
/// qBittorrent entries of deleted files: a failed entry is replaced when the release is downloaded again, and
/// finished torrents in the libraries whose file is gone are removed (without deleting files).
/// </summary>
public sealed class TorrentCleanupTests : IDisposable
{
    private const string Hash = "164ba1d9e6a482263f11ff5a16ee5dc1b0ad853e";
    private const string Magnet = "magnet:?xt=urn:btih:" + Hash;

    private readonly IOptionsMonitor<QBitTorrentOptions> _options = Mock.Of<IOptionsMonitor<QBitTorrentOptions>>(
        o => o.CurrentValue == new QBitTorrentOptions { Url = "http://qbit", Username = "u", Password = "p" });

    private readonly string _library = Path.Combine(Path.GetTempPath(), "pf-torrents-" + Guid.NewGuid().ToString("N"));

    public TorrentCleanupTests() => Directory.CreateDirectory(_library);

    public void Dispose() => Directory.Delete(_library, true);

    [Fact]
    public async Task Add_ReplacesAFailedEntry()
    {
        var handler = new FakeQBitTorrent { State = "missingFiles" };
        using var client = new QBitTorrentClient(_options, NullLogger<QBitTorrentClient>.Instance, handler);

        var hash = await client.AddAsync(Magnet, @"C:\Anime\Show\Season 1", TestContext.Current.CancellationToken);

        Assert.Equal(Hash, hash);
        Assert.Equal(["delete hashes=" + Hash + "&deleteFiles=false", "add"], handler.Actions);
    }

    [Fact]
    public async Task Add_KeepsAWorkingEntry()
    {
        var handler = new FakeQBitTorrent { State = "downloading" };
        using var client = new QBitTorrentClient(_options, NullLogger<QBitTorrentClient>.Instance, handler);

        await client.AddAsync(Magnet, @"C:\Anime\Show\Season 1", TestContext.Current.CancellationToken);

        Assert.Empty(handler.Actions);
    }

    [Fact]
    public async Task RemovesFinishedTorrentsWhoseFileIsGone()
    {
        var kept = Path.Combine(_library, "kept.mkv");
        await File.WriteAllTextAsync(kept, "x", TestContext.Current.CancellationToken);

        var qBit = new Mock<QBitTorrentClient>(_options, NullLogger<QBitTorrentClient>.Instance);
        qBit.Setup(q => q.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(
        [
            new TorrentInfo("deleted", "stoppedUP", 1, true, false, null, Path.Combine(_library, "deleted.mkv")),
            new TorrentInfo("missing", "missingFiles", 0, false, true, null, Path.Combine(_library, "missing.mkv")),
            new TorrentInfo("kept", "stoppedUP", 1, true, false, null, kept),
            new TorrentInfo("unfinished", "downloading", 0.5, false, false, null, Path.Combine(_library, "unfinished.mkv")),
            new TorrentInfo("elsewhere", "stoppedUP", 1, true, false, null, Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "other.mkv"))
        ]);

        var libraryManager = new Mock<ILibraryManager>();
        libraryManager.Setup(l => l.GetVirtualFolders()).Returns([new VirtualFolderInfo { Name = "Anime", Locations = [_library] }]);

        using var manager = new DownloadManager(
            qBit.Object,
            libraryManager.Object,
            Mock.Of<IProviderManager>(),
            Mock.Of<IFileSystem>(),
            _options,
            new LibraryStorage(libraryManager.Object, _options, NullLogger<LibraryStorage>.Instance),
            Mock.Of<IApplicationPaths>(),
            NullLogger<DownloadManager>.Instance);

        await manager.RemoveDeletedTorrentsAsync(TestContext.Current.CancellationToken);

        qBit.Verify(q => q.DeleteAsync("deleted", false, It.IsAny<CancellationToken>()), Times.Once);
        qBit.Verify(q => q.DeleteAsync("missing", false, It.IsAny<CancellationToken>()), Times.Once);
        qBit.Verify(q => q.DeleteAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    /// <summary>A qBittorrent with one torrent (until it's deleted) that records adds and deletes.</summary>
    private sealed class FakeQBitTorrent : HttpMessageHandler
    {
        private bool _present = true;

        public string State { get; init; } = string.Empty;

        public List<string> Actions { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            switch (path)
            {
                case "/api/v2/torrents/info":
                    var json = _present ? $"[{{\"hash\":\"{Hash}\",\"state\":\"{State}\",\"progress\":0}}]" : "[]";
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) };
                case "/api/v2/torrents/delete":
                    _present = false;
                    Actions.Add("delete " + body);
                    break;
                case "/api/v2/torrents/add":
                    Actions.Add("add");
                    break;
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("Ok.") };
        }
    }
}
