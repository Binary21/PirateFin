using MediaBrowser.Controller.Entities;

namespace Emby.Server.Implementations.PirateFin.Library;

/// <summary>
/// A folder of a library (e.g. "E:\Shows") and its folder item, the parent of the series / movies created in it.
/// </summary>
/// <param name="Path">The folder.</param>
/// <param name="Folder">The folder item.</param>
public sealed record PirateFinLocation(string Path, BaseItem Folder);
