namespace Emby.Server.Implementations.PirateFin.Torrents;

/// <summary>
/// A file inside a torrent.
/// </summary>
/// <param name="Index">The file index, used to change its priority.</param>
/// <param name="Name">The path relative to the save path, '/' separated.</param>
/// <param name="Size">The size in bytes.</param>
/// <param name="FirstPiece">The torrent's first piece holding part of the file; <c>null</c> when qBittorrent doesn't say.</param>
/// <param name="LastPiece">The torrent's last piece holding part of the file; <c>null</c> when qBittorrent doesn't say.</param>
public sealed record TorrentFile(int Index, string Name, long Size, int? FirstPiece = null, int? LastPiece = null);
