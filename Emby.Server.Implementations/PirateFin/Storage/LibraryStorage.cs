using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Emby.Server.Implementations.PirateFin.Library;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.PirateFin;
using MediaBrowser.Model.PirateFin;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Emby.Server.Implementations.PirateFin.Storage;

/// <summary>
/// Spreads PirateFin downloads over the drives of a library's folders.
/// </summary>
/// <remarks>
/// New series and movies are created in the library folder whose drive has the most free space. A download goes to the
/// drive its folder (season / movie folder) is on; when that drive is full, the folder becomes a directory junction to
/// "&lt;other drive&gt;\PirateFin Storage\&lt;library&gt;\..." on the library drive with the most room. The library path
/// (and so every item id) stays the same; only where the bytes live changes. The overflow folder is outside every library
/// folder, so the library never shows the media twice.
/// </remarks>
public sealed class LibraryStorage : ILibraryStorage
{
    /// <summary>
    /// The folder, at the root of a drive, that holds media moved off a full drive.
    /// </summary>
    public const string OverflowFolderName = "PirateFin Storage";

    private const long BytesPerGb = 1024L * 1024 * 1024;

    private readonly ILibraryManager _libraryManager;
    private readonly IOptionsMonitor<QBitTorrentOptions> _options;
    private readonly ILogger<LibraryStorage> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="LibraryStorage"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="options">The qBittorrent options (minimum free space).</param>
    /// <param name="logger">The logger.</param>
    public LibraryStorage(ILibraryManager libraryManager, IOptionsMonitor<QBitTorrentOptions> options, ILogger<LibraryStorage> logger)
    {
        _libraryManager = libraryManager;
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// Gets the free space a drive must keep, in bytes.
    /// </summary>
    internal long MinimumFreeBytes => Math.Max(0, _options.CurrentValue.MinimumFreeSpaceGb) * BytesPerGb;

    /// <inheritdoc />
    public bool CanStore(BaseItem item)
    {
        if (item is Video video)
        {
            return Plan(video) is not null;
        }

        // A season / series can be downloaded when any drive of its library has room.
        return GetLibraryFolders(item).Any(location => !IsFull(location));
    }

    /// <inheritdoc />
    public bool HasRoomAt(string path) => !IsFull(path);

    /// <inheritdoc />
    public IReadOnlyList<StorageLocation> GetLocations()
        => _libraryManager.GetVirtualFolders()
            .Where(vf => PirateFinLibraries.Normalize(vf.Name) is not null)
            .SelectMany(vf => vf.Locations.Select(path => (Library: vf.Name, Path: path)))
            .Select(l =>
            {
                var drive = GetDrive(l.Path);
                return new StorageLocation
                {
                    Library = l.Library,
                    Path = l.Path,
                    FreeBytes = drive?.AvailableFreeSpace ?? 0,
                    TotalBytes = drive?.TotalSize ?? 0,
                    IsFull = drive is null || drive.AvailableFreeSpace < MinimumFreeBytes
                };
            })
            .ToList();

    /// <summary>
    /// Gets the library folder new series and movies are created in: the one whose drive has the most free space.
    /// </summary>
    /// <param name="library">The library.</param>
    /// <returns>The folder.</returns>
    public PirateFinLocation ChooseNewItemLocation(PirateFinLibrary library)
        => library.Locations.MaxBy(location => GetDrive(location.Path)?.AvailableFreeSpace ?? 0) ?? library.Locations[0];

    /// <summary>
    /// Makes the folder an item downloads into ready on a drive with room: as it is when its drive has room, otherwise
    /// moved to (and linked from) another drive of the library.
    /// </summary>
    /// <param name="item">The episode or movie.</param>
    /// <param name="isFolderBusy">Whether a running download writes into a folder; such a folder can't be moved.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The folder to download into, or <c>null</c> when there's no room (or it can't be moved yet).</returns>
    public string? PrepareFolder(Video item, Func<string, bool> isFolderBusy, CancellationToken cancellationToken)
    {
        if (Plan(item) is not var (folder, overflow))
        {
            return null;
        }

        if (overflow is null)
        {
            Directory.CreateDirectory(folder);
            return folder;
        }

        if (isFolderBusy(folder))
        {
            _logger.LogInformation("The drive of {Folder} is full, but it can't be moved while a download writes into it", folder);
            return null;
        }

        return MoveToOverflow(folder, overflow, cancellationToken) ? folder : null;
    }

    // The folder the item downloads into, and the overflow folder to move it to when its drive is full (null to download
    // in place). Null when there's no room anywhere.
    private (string Folder, string? Overflow)? Plan(Video item)
    {
        var folder = item.ContainingFolderPath;
        if (string.IsNullOrEmpty(folder))
        {
            return null;
        }

        if (!IsFull(folder))
        {
            return (folder, null);
        }

        var libraryFolders = GetLibraryFolders(item);
        var location = libraryFolders.FirstOrDefault(l => IsUnder(folder, l));

        // A movie placeholder from before movies got their own folders downloads into the library folder itself, which
        // can't be moved.
        if (location is null || string.Equals(Path.TrimEndingDirectorySeparator(location), Path.TrimEndingDirectorySeparator(folder), StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var currentDrive = GetDrive(folder)?.Name;
        var target = libraryFolders
            .Select(GetDrive)
            .OfType<DriveInfo>()
            .Where(d => !string.Equals(d.Name, currentDrive, StringComparison.OrdinalIgnoreCase) && d.AvailableFreeSpace >= MinimumFreeBytes)
            .MaxBy(d => d.AvailableFreeSpace);
        if (target is null)
        {
            return null;
        }

        var library = GetLibraryName(item) ?? "Media";
        var relative = Path.GetRelativePath(location, folder);
        return (folder, Path.Combine(target.RootDirectory.FullName, OverflowFolderName, library, relative));
    }

    private bool MoveToOverflow(string folder, string overflow, CancellationToken cancellationToken)
    {
        try
        {
            Directory.CreateDirectory(overflow);
            if (Directory.Exists(folder))
            {
                if (new DirectoryInfo(folder).LinkTarget is not null)
                {
                    // Already linked to a drive that's full now too; the overnight move sorts that out.
                    return false;
                }

                if (!MoveContents(folder, overflow, cancellationToken))
                {
                    return false;
                }

                Directory.Delete(folder);
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(folder)!);
            }

            CreateJunction(folder, overflow);
            _logger.LogInformation("{Folder} is on a full drive; it's stored in {Overflow} from now on", folder, overflow);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Could not move {Folder} to {Overflow}", folder, overflow);
            return false;
        }
    }

    // Moves the folder's files across drives; on any failure (e.g. an episode being played) the moved ones go back.
    private bool MoveContents(string folder, string target, CancellationToken cancellationToken)
    {
        var moved = new List<(string From, string To)>();
        try
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(folder).ToList())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var destination = Path.Combine(target, Path.GetFileName(entry));
                if (Directory.Exists(entry))
                {
                    Directory.Move(entry, destination);
                }
                else
                {
                    File.Move(entry, destination);
                }

                moved.Add((entry, destination));
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogInformation(ex, "Could not move {Folder} to another drive yet (a file may be in use); trying again later", folder);
            foreach (var (from, to) in Enumerable.Reverse(moved))
            {
                try
                {
                    if (Directory.Exists(to))
                    {
                        Directory.Move(to, from);
                    }
                    else
                    {
                        File.Move(to, from);
                    }
                }
                catch (Exception rollbackEx) when (rollbackEx is IOException or UnauthorizedAccessException)
                {
                    _logger.LogError(rollbackEx, "Could not move {File} back to {Folder}", to, folder);
                }
            }

            return false;
        }
    }

    /// <summary>
    /// Links a folder to another folder (on any local drive). Directory junctions work across local drives without the
    /// symlink privilege Windows requires for symbolic links.
    /// </summary>
    /// <param name="link">The folder to create, pointing at <paramref name="target"/>.</param>
    /// <param name="target">The existing folder.</param>
    internal static void CreateJunction(string link, string target)
    {
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(link, target);
            return;
        }

        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            ArgumentList = { "/c", "mklink", "/J", link, target },
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        }) ?? throw new InvalidOperationException("Could not start mklink.");

        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0 || !Directory.Exists(link))
        {
            throw new InvalidOperationException($"mklink /J failed: {error.Trim()}");
        }
    }

    private static bool IsUnder(string path, string folder)
    {
        var relative = Path.GetRelativePath(folder, path);
        return !relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative);
    }

    /// <summary>
    /// Gets the drive a path's bytes are on: follows junctions / links of the path or its nearest existing parent.
    /// </summary>
    /// <param name="path">The path.</param>
    /// <returns>The drive, or <c>null</c>.</returns>
    internal static DriveInfo? GetDrive(string path)
    {
        try
        {
            var existing = path;
            while (!string.IsNullOrEmpty(existing) && !Directory.Exists(existing))
            {
                existing = Path.GetDirectoryName(existing);
            }

            if (string.IsNullOrEmpty(existing))
            {
                return null;
            }

            for (var dir = new DirectoryInfo(existing); dir is not null; dir = dir.Parent)
            {
                if (dir.LinkTarget is not null && dir.ResolveLinkTarget(true) is { } target)
                {
                    return new DriveInfo(Path.GetPathRoot(target.FullName)!);
                }
            }

            return new DriveInfo(Path.GetPathRoot(existing)!);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private bool IsFull(string path) => GetDrive(path) is not { } drive || drive.AvailableFreeSpace < MinimumFreeBytes;

    private IReadOnlyList<string> GetLibraryFolders(BaseItem item)
        => _libraryManager.GetCollectionFolders(item)
            .OfType<CollectionFolder>()
            .SelectMany(f => f.PhysicalLocations)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private string? GetLibraryName(BaseItem item)
        => _libraryManager.GetCollectionFolders(item).Select(f => f.Name).FirstOrDefault();
}
