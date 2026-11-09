using System;
using System.Collections.Generic;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.PirateFin;

namespace MediaBrowser.Controller.PirateFin;

/// <summary>
/// Downloads episodes and movies through qBittorrent and tracks their progress.
/// </summary>
public interface IDownloadManager
{
    /// <summary>
    /// Occurs when an episode or movie has finished downloading.
    /// </summary>
    event EventHandler<BaseItem>? DownloadCompleted;

    /// <summary>
    /// Occurs when a downloading episode or movie has become playable (part way through its download).
    /// </summary>
    event EventHandler<BaseItem>? DownloadPlayable;

    /// <summary>
    /// Occurs when an episode or movie download may have changed (after each check of qBittorrent, and when it's queued,
    /// started, failed or finished). Carries the item id; read <see cref="GetState"/> / <see cref="GetProgress"/>.
    /// </summary>
    event EventHandler<Guid>? DownloadUpdated;

    /// <summary>
    /// Gets or sets a value indicating whether someone follows a download live (a Play button waiting for it): qBittorrent
    /// is then checked every 2 seconds instead of every <c>MonitorPollSeconds</c>.
    /// </summary>
    bool PollQuickly { get; set; }

    /// <summary>
    /// Gets where an item's download stands.
    /// </summary>
    /// <param name="itemId">The item id.</param>
    /// <returns>The state.</returns>
    DownloadState GetState(Guid itemId);

    /// <summary>
    /// Gets a value indicating whether a download is slow: 30 seconds after it started, qBittorrent expects it to take
    /// more than an hour (or can't tell), and it isn't playable yet.
    /// </summary>
    /// <param name="itemId">The episode or movie id.</param>
    /// <returns><c>true</c> when it's slow.</returns>
    bool IsSlow(Guid itemId);

    /// <summary>
    /// Gets qBittorrent's estimate of how long a download still takes.
    /// </summary>
    /// <param name="itemId">The episode or movie id.</param>
    /// <returns>The time left; <c>null</c> when it isn't downloading or qBittorrent can't tell.</returns>
    TimeSpan? GetTimeLeft(Guid itemId);

    /// <summary>
    /// Queues an episode or movie for download using its <see cref="IPirateFinDownloadable.DownloadUrl"/>.
    /// Does nothing if it is not downloadable, or already queued, downloading or downloaded.
    /// </summary>
    /// <param name="item">The episode or movie.</param>
    /// <returns><c>true</c> if the item was queued.</returns>
    bool QueueDownload(BaseItem item);

    /// <summary>
    /// Gets how far the download of an item is, as reported by qBittorrent on the last poll.
    /// </summary>
    /// <param name="itemId">The episode or movie id.</param>
    /// <returns>The progress between 0 and 1, or <c>null</c> when the item isn't downloading.</returns>
    double? GetProgress(Guid itemId);

    /// <summary>
    /// Gets how much of an item's video is downloaded without a gap from its start, on the last poll: how far a player
    /// can get before it reaches data that isn't there yet. The overall progress when qBittorrent can't tell.
    /// </summary>
    /// <param name="itemId">The episode or movie id.</param>
    /// <returns>Between 0 and 1, or <c>null</c> when the item isn't downloading.</returns>
    double? GetContiguousProgress(Guid itemId);

    /// <summary>
    /// Gets the items that are queued, waiting for storage or downloading.
    /// </summary>
    /// <returns>Their ids.</returns>
    IReadOnlyCollection<Guid> GetPendingDownloads();

    /// <summary>
    /// Gets a value indicating whether a running download writes into the folder or one of its subfolders.
    /// </summary>
    /// <param name="folder">The folder, e.g. a series folder.</param>
    /// <returns><c>true</c> when something downloads there.</returns>
    bool IsDownloadingUnder(string folder);
}
