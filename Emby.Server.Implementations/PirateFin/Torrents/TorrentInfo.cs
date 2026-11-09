namespace Emby.Server.Implementations.PirateFin.Torrents;

/// <summary>
/// The state of a torrent in qBittorrent.
/// </summary>
/// <param name="Hash">The info hash.</param>
/// <param name="State">The qBittorrent state.</param>
/// <param name="Progress">The progress, 0-1.</param>
/// <param name="IsComplete">Whether the download has finished.</param>
/// <param name="IsFailed">Whether the torrent errored.</param>
/// <param name="EtaSeconds">qBittorrent's estimate of the time left, in seconds (8640000 when it can't tell, e.g. no
/// peers); <c>null</c> when not reported.</param>
/// <param name="ContentPath">The torrent's file (single-file torrents) or folder on disk; <c>null</c> when not reported.</param>
public sealed record TorrentInfo(string Hash, string State, double Progress, bool IsComplete, bool IsFailed, long? EtaSeconds = null, string? ContentPath = null);
