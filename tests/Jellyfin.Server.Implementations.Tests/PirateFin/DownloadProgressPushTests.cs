using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Emby.Server.Implementations.PirateFin.Downloads;
using Emby.Server.Implementations.PirateFin.Torrents;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Net.WebSocketMessages;
using MediaBrowser.Controller.PirateFin;
using MediaBrowser.Model.PirateFin;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.PirateFin;

/// <summary>
/// The Play button's download status: qBittorrent's states as shown, and the pushes to the pages following an item.
/// </summary>
public sealed class DownloadProgressPushTests : IDisposable
{
    private readonly Episode _episode = new() { Id = Guid.NewGuid(), IsInternet = true, IsDownloaded = false };
    private readonly Mock<IDownloadManager> _downloadManager = new();
    private readonly Mock<IDownloadProgress> _progress = new();
    private readonly Mock<ILibraryManager> _libraryManager = new();
    private readonly List<(IWebSocketConnection Connection, DownloadProgressInfo Info)> _sent = [];
    private readonly DownloadProgressWebSocketListener _listener;
    private DownloadProgressInfo _status;

    public DownloadProgressPushTests()
    {
        _status = new DownloadProgressInfo { ItemId = _episode.Id, State = DownloadState.Downloading, Progress = 0.05, PlayableFill = 0.5 };
        _downloadManager.SetupProperty(d => d.PollQuickly);
        _progress.Setup(p => p.Get(_episode)).Returns(() => _status);
        _libraryManager.Setup(l => l.GetItemById<BaseItem>(_episode.Id, It.IsAny<User?>())).Returns(_episode);
        _libraryManager.Setup(l => l.GetItemById(_episode.Id)).Returns(_episode);
        _listener = new DownloadProgressWebSocketListener(
            _downloadManager.Object,
            _progress.Object,
            _libraryManager.Object,
            NullLogger<DownloadProgressWebSocketListener>.Instance);
    }

    public void Dispose() => _listener.Dispose();

    [Theory]
    [InlineData("metaDL", null, DownloadState.Downloading)]
    [InlineData("downloading", null, DownloadState.Downloading)]
    [InlineData("checkingDL", null, DownloadState.Downloading)]
    [InlineData("stalledDL", 10, DownloadState.Downloading)]
    [InlineData("stalledDL", 30, DownloadState.Stalled)]
    [InlineData("queuedDL", null, DownloadState.Queued)]
    [InlineData("stoppedDL", null, DownloadState.Paused)]
    [InlineData("pausedDL", null, DownloadState.Paused)]
    [InlineData("missingFiles", null, DownloadState.Failed)]
    [InlineData("", null, DownloadState.Downloading)]
    public void MapsQBitTorrentStates(string state, int? stalledSeconds, DownloadState expected)
        => Assert.Equal(expected, DownloadManager.ToState(state, stalledSeconds is { } s ? TimeSpan.FromSeconds(s) : null));

    private Mock<IWebSocketConnection> Connection()
    {
        var connection = new Mock<IWebSocketConnection>();
        connection.SetupGet(c => c.State).Returns(WebSocketState.Open);
        connection.SetupGet(c => c.AuthorizationInfo).Returns(new AuthorizationInfo());
        connection
            .Setup(c => c.SendAsync(It.IsAny<OutboundWebSocketMessage<DownloadProgressInfo>>(), It.IsAny<CancellationToken>()))
            .Callback<OutboundWebSocketMessage<DownloadProgressInfo>, CancellationToken>((m, _) =>
            {
                Assert.Equal(SessionMessageType.PirateFinDownloadProgress, m.MessageType);
                _sent.Add((connection.Object, m.Data!));
            })
            .Returns(Task.CompletedTask);
        return connection;
    }

    private Task Send(IWebSocketConnection connection, SessionMessageType type)
        => _listener.ProcessMessageAsync(new WebSocketMessageInfo { MessageType = type, Data = _episode.Id.ToString(), Connection = connection });

    private void Update() => _downloadManager.Raise(d => d.DownloadUpdated += null, _downloadManager.Object, _episode.Id);

    [Fact]
    public async Task OnlyFollowersGetChangesUntilPlayable()
    {
        var watching = Connection();
        var other = Connection();
        await Send(watching.Object, SessionMessageType.PirateFinDownloadStart);

        // The status straight away; polling quickly while followed.
        Assert.Single(_sent);
        Assert.True(_downloadManager.Object.PollQuickly);

        // Unchanged: nothing sent.
        Update();
        Assert.Single(_sent);

        _status = new DownloadProgressInfo { ItemId = _episode.Id, State = DownloadState.Stalled, Progress = 0.05, PlayableFill = 0.5 };
        Update();
        Assert.Equal(2, _sent.Count);
        Assert.Equal(DownloadState.Stalled, _sent[^1].Info.State);

        _status = new DownloadProgressInfo { ItemId = _episode.Id, State = DownloadState.Downloading, Progress = 0.1, PlayableFill = 1, Playable = true };
        Update();
        Assert.True(_sent[^1].Info.Playable);

        // Dropped once playable.
        Assert.Equal(0, _listener.FollowedCount());
        Assert.False(_downloadManager.Object.PollQuickly);
        Assert.All(_sent, s => Assert.Same(watching.Object, s.Connection));
    }

    [Fact]
    public async Task StopAndClosedConnectionsAreDropped()
    {
        var first = Connection();
        var second = Connection();
        await Send(first.Object, SessionMessageType.PirateFinDownloadStart);
        await Send(second.Object, SessionMessageType.PirateFinDownloadStart);

        await Send(first.Object, SessionMessageType.PirateFinDownloadStop);
        Assert.Equal(1, _listener.FollowedCount());

        second.Raise(c => c.Closed += null, second.Object, EventArgs.Empty);
        Assert.Equal(0, _listener.FollowedCount());
        Assert.False(_downloadManager.Object.PollQuickly);
    }

    [Theory]
    [InlineData(new[] { 2, 2, 2, 2 }, 0, 3, 1.0)]
    [InlineData(new[] { 2, 2, 0, 2 }, 0, 3, 0.5)]
    [InlineData(new[] { 0, 2, 2, 2 }, 0, 3, 0.0)]
    [InlineData(new[] { 2, 1, 2, 2 }, 0, 3, 0.25)]

    // The file's own pieces only (a sample before it in the torrent is skipped).
    [InlineData(new[] { 0, 0, 2, 2, 2, 0 }, 2, 5, 0.75)]
    public void CountsOnlyPiecesWithoutAGapFromTheStart(int[] states, int first, int last, double expected)
        => Assert.Equal(expected, QBitTorrentClient.GetContiguousProgress(states, first, last));

    [Fact]
    public void PiecesNotCoveringTheFileAreUnknown()
        => Assert.Null(QBitTorrentClient.GetContiguousProgress([2, 2], 0, 5));

    [Fact]
    public async Task PlayerFollowsUntilDownloadedWhilePlayButtonsStopAtPlayable()
    {
        var page = Connection();
        var player = Connection();
        await Send(page.Object, SessionMessageType.PirateFinDownloadStart);
        await Send(player.Object, SessionMessageType.PirateFinDownloadFollow);

        _status = new DownloadProgressInfo { ItemId = _episode.Id, State = DownloadState.Downloading, Progress = 0.2, ContiguousProgress = 0.1, PlayableFill = 1, Playable = true };
        Update();

        // The page is done; the player still follows (and polling stays quick).
        Assert.Equal(1, _listener.FollowedCount());
        Assert.True(_downloadManager.Object.PollQuickly);

        _sent.Clear();
        _status = new DownloadProgressInfo { ItemId = _episode.Id, State = DownloadState.Downloading, Progress = 0.3, ContiguousProgress = 0.25, PlayableFill = 1, Playable = true };
        Update();
        Assert.Same(player.Object, Assert.Single(_sent).Connection);

        _status = new DownloadProgressInfo { ItemId = _episode.Id, Progress = 1, ContiguousProgress = 1, PlayableFill = 1, Playable = true, Downloaded = true };
        Update();
        Assert.Equal(0, _listener.FollowedCount());
        Assert.False(_downloadManager.Object.PollQuickly);
    }

    [Fact]
    public async Task PlayerOfADownloadedItemIsAnsweredButNotFollowed()
    {
        _status = new DownloadProgressInfo { ItemId = _episode.Id, Playable = true, Downloaded = true, PlayableFill = 1 };
        await Send(Connection().Object, SessionMessageType.PirateFinDownloadFollow);

        Assert.True(Assert.Single(_sent).Info.Downloaded);
        Assert.Equal(0, _listener.FollowedCount());
    }

    [Fact]
    public async Task AlreadyPlayableIsAnsweredButNotFollowed()
    {
        _status = new DownloadProgressInfo { ItemId = _episode.Id, Playable = true, PlayableFill = 1 };
        await Send(Connection().Object, SessionMessageType.PirateFinDownloadStart);

        Assert.True(Assert.Single(_sent).Info.Playable);
        Assert.Equal(0, _listener.FollowedCount());
    }
}
