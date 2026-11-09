using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Emby.Server.Implementations.PirateFin.Library;

/// <summary>
/// A resolved PirateFin target library: its configured folders (a library can span drives) and their folder items.
/// </summary>
/// <param name="Name">The library name.</param>
/// <param name="Locations">The library's folders; at least one.</param>
/// <param name="CollectionFolderId">The id of the user facing collection folder.</param>
public sealed record PirateFinLibrary(string Name, IReadOnlyList<PirateFinLocation> Locations, Guid CollectionFolderId)
{
    private static readonly char[] _invalidFileNameChars = System.IO.Path.GetInvalidFileNameChars();

    /// <summary>
    /// Resolves a library by name.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="name">The library name.</param>
    /// <param name="logger">The logger used to report misconfiguration.</param>
    /// <returns>The library, or <c>null</c> when it is missing or not configured.</returns>
    public static PirateFinLibrary? Resolve(ILibraryManager libraryManager, string name, ILogger logger)
    {
        var virtualFolder = libraryManager.GetVirtualFolders()
            .FirstOrDefault(vf => vf.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (virtualFolder is null)
        {
            logger.LogError("PirateFin library {Library} does not exist; create it in the dashboard", name);
            return null;
        }

        var locations = new List<PirateFinLocation>();
        foreach (var path in virtualFolder.Locations.Where(p => !string.IsNullOrEmpty(p)))
        {
            var folder = libraryManager.GetItemList(new InternalItemsQuery { Path = path, IsFolder = true }).FirstOrDefault();
            if (folder is null)
            {
                logger.LogError("No library folder item found for {Library} at {Path}; run a library scan first", name, path);
                continue;
            }

            locations.Add(new PirateFinLocation(path, folder));
        }

        if (locations.Count == 0)
        {
            logger.LogError("PirateFin library {Library} has no usable folder configured", name);
            return null;
        }

        Guid.TryParse(virtualFolder.ItemId, out var collectionFolderId);
        return new PirateFinLibrary(virtualFolder.Name, locations, collectionFolderId);
    }

    /// <summary>
    /// Removes characters that are not allowed in file names.
    /// </summary>
    /// <param name="name">The name.</param>
    /// <returns>The sanitized name.</returns>
    public static string SanitizeFileName(string name)
        => new string(name.Where(ch => !_invalidFileNameChars.Contains(ch)).ToArray()).Trim();
}
