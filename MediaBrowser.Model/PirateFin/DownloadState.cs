namespace MediaBrowser.Model.PirateFin;

/// <summary>
/// Where an episode or movie download stands, as the Play button shows it.
/// </summary>
public enum DownloadState
{
    /// <summary>
    /// Not downloading.
    /// </summary>
    None,

    /// <summary>
    /// Waiting to start: not sent to qBittorrent yet (queued, or waiting for a drive with room), or queued in qBittorrent.
    /// </summary>
    Queued,

    /// <summary>
    /// Downloading (also while qBittorrent fetches the magnet's metadata or checks the files).
    /// </summary>
    Downloading,

    /// <summary>
    /// qBittorrent has reported the download as stalled (no data coming in) for a while.
    /// </summary>
    Stalled,

    /// <summary>
    /// Paused / stopped in qBittorrent.
    /// </summary>
    Paused,

    /// <summary>
    /// Failed (qBittorrent error or missing files); opening the item starts it again.
    /// </summary>
    Failed
}
