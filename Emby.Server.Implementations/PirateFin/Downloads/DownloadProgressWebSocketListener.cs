using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Net.WebSocketMessages;
using MediaBrowser.Controller.PirateFin;
using MediaBrowser.Model.PirateFin;
using MediaBrowser.Model.Session;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Emby.Server.Implementations.PirateFin.Downloads;

/// <summary>
/// Pushes an episode or movie's download status to the pages waiting for it to become playable, over the WebSocket
/// they already have open.
/// </summary>
/// <remarks>
/// A page sends <see cref="SessionMessageType.PirateFinDownloadStart"/> with the item id; it gets the status straight
/// away and then each time it changes (<see cref="SessionMessageType.PirateFinDownloadProgress"/>), and nobody else
/// does. Once the item is playable it's sent one last time and its followers are dropped. The player sends
/// <see cref="SessionMessageType.PirateFinDownloadFollow"/> instead: it's kept until the item is fully downloaded, to
/// pause before it reaches data that isn't there yet. A page leaving sends
/// <see cref="SessionMessageType.PirateFinDownloadStop"/>; a closed connection is dropped too. While anything is
/// followed, qBittorrent is checked every 2 seconds instead of every <c>MonitorPollSeconds</c>.
/// </remarks>
public sealed class DownloadProgressWebSocketListener : IWebSocketListener, IDisposable
{
    private readonly IDownloadManager _downloadManager;
    private readonly IDownloadProgress _progress;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<DownloadProgressWebSocketListener> _logger;
    private readonly Lock _lock = new();

    // The connections following each item (true: until it's downloaded, else until it's playable), and what each
    // item was last sent as.
    private readonly Dictionary<Guid, Dictionary<IWebSocketConnection, bool>> _followers = [];
    private readonly Dictionary<Guid, DownloadProgressInfo> _lastSent = [];
    private readonly HashSet<IWebSocketConnection> _watchedConnections = [];

    /// <summary>
    /// Initializes a new instance of the <see cref="DownloadProgressWebSocketListener"/> class.
    /// </summary>
    /// <param name="downloadManager">The download manager.</param>
    /// <param name="progress">The download status.</param>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="logger">The logger.</param>
    public DownloadProgressWebSocketListener(
        IDownloadManager downloadManager,
        IDownloadProgress progress,
        ILibraryManager libraryManager,
        ILogger<DownloadProgressWebSocketListener> logger)
    {
        _downloadManager = downloadManager;
        _progress = progress;
        _libraryManager = libraryManager;
        _logger = logger;
        _downloadManager.DownloadUpdated += OnDownloadUpdated;
    }

    /// <inheritdoc />
    public Task ProcessMessageAsync(WebSocketMessageInfo message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.MessageType == SessionMessageType.PirateFinDownloadStart)
        {
            Start(message.Connection, message.Data, false);
        }
        else if (message.MessageType == SessionMessageType.PirateFinDownloadFollow)
        {
            Start(message.Connection, message.Data, true);
        }
        else if (message.MessageType == SessionMessageType.PirateFinDownloadStop)
        {
            Stop(message.Connection, message.Data);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task ProcessWebSocketConnectedAsync(IWebSocketConnection connection, HttpContext httpContext) => Task.CompletedTask;

    /// <inheritdoc />
    public void Dispose()
    {
        _downloadManager.DownloadUpdated -= OnDownloadUpdated;
    }

    /// <summary>
    /// Gets the number of items followed (tests).
    /// </summary>
    /// <returns>How many items have followers.</returns>
    internal int FollowedCount()
    {
        lock (_lock)
        {
            return _followers.Count;
        }
    }

    private void Start(IWebSocketConnection connection, string? data, bool untilDownloaded)
    {
        if (!Guid.TryParse(data, out var itemId))
        {
            return;
        }

        // Only items the connection's user can see.
        if (_libraryManager.GetItemById<BaseItem>(itemId, connection.AuthorizationInfo?.User) is not Video video)
        {
            return;
        }

        var info = _progress.Get(video);
        if (!IsDone(info, untilDownloaded))
        {
            lock (_lock)
            {
                if (!_followers.TryGetValue(itemId, out var followers))
                {
                    followers = [];
                    _followers[itemId] = followers;
                }

                // A page and the player on the same connection: the longer follow wins.
                followers[connection] = untilDownloaded || followers.GetValueOrDefault(connection);
                _lastSent[itemId] = info;
                if (_watchedConnections.Add(connection))
                {
                    connection.Closed += OnConnectionClosed;
                }

                UpdatePolling();
            }
        }

        Send([connection], info);
    }

    private void Stop(IWebSocketConnection connection, string? data)
    {
        lock (_lock)
        {
            var itemIds = Guid.TryParse(data, out var itemId) ? [itemId] : _followers.Keys.ToList();
            foreach (var id in itemIds)
            {
                Remove(id, connection);
            }

            UpdatePolling();
        }
    }

    private void OnConnectionClosed(object? sender, EventArgs e)
    {
        if (sender is not IWebSocketConnection connection)
        {
            return;
        }

        connection.Closed -= OnConnectionClosed;
        lock (_lock)
        {
            _watchedConnections.Remove(connection);
            foreach (var id in _followers.Keys.ToList())
            {
                Remove(id, connection);
            }

            UpdatePolling();
        }
    }

    private void OnDownloadUpdated(object? sender, Guid itemId)
    {
        IWebSocketConnection[] connections;
        lock (_lock)
        {
            if (!_followers.TryGetValue(itemId, out var followers))
            {
                return;
            }

            connections = followers.Keys.Where(c => c.State == WebSocketState.Open).ToArray();
        }

        if (_libraryManager.GetItemById(itemId) is not Video video)
        {
            // Deleted.
            Drop(itemId);
            return;
        }

        var info = _progress.Get(video);
        lock (_lock)
        {
            // Only changes go out.
            if (_lastSent.TryGetValue(itemId, out var last) && IsSame(last, info))
            {
                return;
            }

            _lastSent[itemId] = info;
        }

        Send(connections, info);
        if (info.Playable)
        {
            // Nothing left to follow for the Play buttons (they turn white and stop listening); the player stays
            // until the download is complete.
            DropDone(itemId, info);
        }
    }

    private static bool IsDone(DownloadProgressInfo info, bool untilDownloaded)
        => untilDownloaded ? info.Downloaded : info.Playable;

    private static bool IsSame(DownloadProgressInfo a, DownloadProgressInfo b)
        => a.Playable == b.Playable
            && a.Downloaded == b.Downloaded
            && a.State == b.State
            && a.Progress == b.Progress
            && a.ContiguousProgress == b.ContiguousProgress
            && a.PlayableSeconds == b.PlayableSeconds
            && a.Slow == b.Slow;

    private void DropDone(Guid itemId, DownloadProgressInfo info)
    {
        lock (_lock)
        {
            if (_followers.TryGetValue(itemId, out var followers))
            {
                foreach (var (connection, untilDownloaded) in followers.ToList())
                {
                    if (IsDone(info, untilDownloaded))
                    {
                        Remove(itemId, connection);
                    }
                }
            }

            UpdatePolling();
        }
    }

    private void Drop(Guid itemId)
    {
        lock (_lock)
        {
            _followers.Remove(itemId);
            _lastSent.Remove(itemId);
            UpdatePolling();
        }
    }

    // Under _lock.
    private void Remove(Guid itemId, IWebSocketConnection connection)
    {
        if (_followers.TryGetValue(itemId, out var followers) && followers.Remove(connection) && followers.Count == 0)
        {
            _followers.Remove(itemId);
            _lastSent.Remove(itemId);
        }
    }

    // Under _lock.
    private void UpdatePolling() => _downloadManager.PollQuickly = _followers.Count > 0;

    private void Send(IEnumerable<IWebSocketConnection> connections, DownloadProgressInfo info)
    {
        foreach (var connection in connections)
        {
            _ = SendAsync(connection, info);
        }
    }

    private async Task SendAsync(IWebSocketConnection connection, DownloadProgressInfo info)
    {
        try
        {
            await connection.SendAsync(
                new OutboundWebSocketMessage<DownloadProgressInfo> { MessageType = SessionMessageType.PirateFinDownloadProgress, Data = info },
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not send the download status of {ItemId}", info.ItemId);
        }
    }
}
