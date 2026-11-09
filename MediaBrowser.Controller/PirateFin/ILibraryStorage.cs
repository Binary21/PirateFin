using System.Collections.Generic;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.PirateFin;

namespace MediaBrowser.Controller.PirateFin;

/// <summary>
/// Decides which drive PirateFin downloads go to. A drive is full once it has less than
/// <see cref="QBitTorrentOptions.MinimumFreeSpaceGb"/> free; downloads pause when no drive of the library has room.
/// </summary>
public interface ILibraryStorage
{
    /// <summary>
    /// Gets a value indicating whether the item can be downloaded now: its own drive, or another drive of its library,
    /// has room.
    /// </summary>
    /// <param name="item">The episode, movie, season or series.</param>
    /// <returns><c>false</c> when downloads for it are paused because storage is full.</returns>
    bool CanStore(BaseItem item);

    /// <summary>
    /// Gets the folders of every PirateFin library with the free space of their drives.
    /// </summary>
    /// <returns>The locations.</returns>
    IReadOnlyList<StorageLocation> GetLocations();

    /// <summary>
    /// Gets a value indicating whether the drive holding a folder (following junctions) has more than the minimum free
    /// space.
    /// </summary>
    /// <param name="path">The folder.</param>
    /// <returns><c>true</c> if something more can be stored there.</returns>
    bool HasRoomAt(string path);
}
