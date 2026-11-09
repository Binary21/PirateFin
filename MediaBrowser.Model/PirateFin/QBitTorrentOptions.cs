namespace MediaBrowser.Model.PirateFin;

/// <summary>
/// qBittorrent settings, loaded from the "QBitTorrent" section of qbittorrent.json.
/// </summary>
public sealed class QBitTorrentOptions
{
    /// <summary>
    /// The name of the configuration section.
    /// </summary>
    public const string SectionName = "QBitTorrent";

    /// <summary>
    /// Gets or sets the qBittorrent Web UI url.
    /// </summary>
    public string Url { get; set; } = "http://localhost:8080/";

    /// <summary>
    /// Gets or sets the Web UI user name.
    /// </summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the Web UI password.
    /// </summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the path of the qBittorrent executable, started on demand when it is not running. Empty to never start it.
    /// </summary>
    public string ExecutablePath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets how often active downloads are polled, in seconds.
    /// </summary>
    public int MonitorPollSeconds { get; set; } = 10;

    /// <summary>
    /// Gets or sets the download progress (0-1) at which an episode becomes playable.
    /// </summary>
    public double PlayableProgress { get; set; } = 0.10;

    /// <summary>
    /// Gets or sets the free space (in GB) a drive must keep; downloads don't go to a drive with less.
    /// </summary>
    public int MinimumFreeSpaceGb { get; set; } = 25;

    /// <summary>
    /// Gets or sets when (server time, "HH:mm") series split across drives start being moved back together.
    /// </summary>
    public string StorageMoveStart { get; set; } = "02:00";

    /// <summary>
    /// Gets or sets when (server time, "HH:mm") moving split series stops for the night.
    /// </summary>
    public string StorageMoveEnd { get; set; } = "06:00";
}
