using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.PirateFin;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.PirateFin;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Emby.Server.Implementations.PirateFin.Storage;

/// <summary>
/// Moves series that were split across drives (a season went to another drive because the series' drive was full)
/// back onto one drive: the library drive with the most free space that can hold all of it. Runs in the configured
/// night window, one series at a time, skipping series being played or downloaded.
/// </summary>
/// <remarks>
/// Like <see cref="LibraryStorage"/>, the library path never changes: the series ends up as a real folder in its library
/// folder (when that's on the chosen drive) or as a junction to "&lt;drive&gt;\PirateFin Storage\&lt;library&gt;\&lt;series&gt;".
/// Files from other drives are copied into a staging folder on the chosen drive first; only when every copy succeeded
/// are the originals removed, so a failure (e.g. a locked file) leaves the series as it was.
/// </remarks>
public sealed class StorageRebalancer : BackgroundService, IStorageRebalancer
{
    private static readonly TimeSpan _checkInterval = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<string, string> _lastAttempts = new(StringComparer.OrdinalIgnoreCase);

    private readonly ILibraryManager _libraryManager;
    private readonly ILibraryMonitor _libraryMonitor;
    private readonly ISessionManager _sessionManager;
    private readonly LibraryStorage _storage;
    private readonly IDownloadManager _downloadManager;
    private readonly IOptionsMonitor<QBitTorrentOptions> _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<StorageRebalancer> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="StorageRebalancer"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="libraryMonitor">The library monitor, paused for a series while it moves.</param>
    /// <param name="sessionManager">The session manager, to skip series being played.</param>
    /// <param name="storage">The library storage.</param>
    /// <param name="downloadManager">The download manager, to skip series being downloaded.</param>
    /// <param name="options">The qBittorrent options (minimum free space, move window).</param>
    /// <param name="timeProvider">The time provider.</param>
    /// <param name="logger">The logger.</param>
    public StorageRebalancer(
        ILibraryManager libraryManager,
        ILibraryMonitor libraryMonitor,
        ISessionManager sessionManager,
        LibraryStorage storage,
        IDownloadManager downloadManager,
        IOptionsMonitor<QBitTorrentOptions> options,
        TimeProvider timeProvider,
        ILogger<StorageRebalancer> logger)
    {
        _libraryManager = libraryManager;
        _libraryMonitor = libraryMonitor;
        _sessionManager = sessionManager;
        _storage = storage;
        _downloadManager = downloadManager;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    public IReadOnlyList<SplitSeries> GetSplitSeries()
        => FindSplitSeries()
            .Select(split => new SplitSeries
            {
                Name = Path.GetFileName(split.Folder.Path),
                Path = split.Folder.Path,
                BytesPerDrive = split.BytesPerDrive,
                CannotFit = ChooseTarget(split) is null,
                LastAttempt = _lastAttempts.GetValueOrDefault(split.Folder.Path)
            })
            .ToList();

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_checkInterval, _timeProvider);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            if (!IsInMoveWindow())
            {
                continue;
            }

            try
            {
                ReuniteSplitSeries(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Moving split series failed");
            }
        }
    }

    /// <summary>
    /// Gets a value indicating whether the server's local time is in the configured move window (which may span midnight).
    /// </summary>
    /// <returns><c>true</c> during the window.</returns>
    internal bool IsInMoveWindow() => IsInMoveWindow(_options.CurrentValue, TimeOnly.FromDateTime(_timeProvider.GetLocalNow().DateTime));

    /// <summary>
    /// Gets a value indicating whether a time of day is in the configured move window (which may span midnight). Other
    /// night jobs use the same window.
    /// </summary>
    /// <param name="options">The qBittorrent options (the window).</param>
    /// <param name="now">The server's local time of day.</param>
    /// <returns><c>true</c> during the window.</returns>
    internal static bool IsInMoveWindow(QBitTorrentOptions options, TimeOnly now)
    {
        if (!TimeOnly.TryParse(options.StorageMoveStart, CultureInfo.InvariantCulture, out var start)
            || !TimeOnly.TryParse(options.StorageMoveEnd, CultureInfo.InvariantCulture, out var end))
        {
            return false;
        }

        return start <= end ? now >= start && now < end : now >= start || now < end;
    }

    /// <summary>
    /// Moves the split series back together now (when in the move window).
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    internal void ReuniteSplitSeries(CancellationToken cancellationToken)
    {
        foreach (var split in FindSplitSeries())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsInMoveWindow())
            {
                return;
            }

            var path = split.Folder.Path;
            if (ChooseTarget(split) is not { } target)
            {
                _lastAttempts[path] = "No drive can hold the whole series";
                continue;
            }

            if (IsBeingPlayed(path))
            {
                _lastAttempts[path] = "Skipped: being played";
                continue;
            }

            if (_downloadManager.IsDownloadingUnder(path))
            {
                _lastAttempts[path] = "Skipped: downloading";
                continue;
            }

            Reunite(split, target, cancellationToken);
        }
    }

    // The library drive with the most free space that can take everything not on it yet and keep the minimum free.
    private string? ChooseTarget(SplitLayout split)
    {
        var total = split.BytesPerDrive.Values.Sum();
        return split.Folder.LibraryLocations
            .Select(LibraryStorage.GetDrive)
            .OfType<DriveInfo>()
            .DistinctBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .Where(d => d.AvailableFreeSpace - (total - split.BytesPerDrive.GetValueOrDefault(d.Name)) >= _storage.MinimumFreeBytes)
            .MaxBy(d => d.AvailableFreeSpace)
            ?.Name;
    }

    private bool IsBeingPlayed(string seriesPath)
    {
        var prefix = Path.TrimEndingDirectorySeparator(seriesPath) + Path.DirectorySeparatorChar;
        return _sessionManager.Sessions.Any(s => s.FullNowPlayingItem?.Path is { } playing
            && playing.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    private IEnumerable<SplitLayout> FindSplitSeries()
    {
        foreach (var folder in FindSeriesFolders())
        {
            SplitLayout? layout = null;
            try
            {
                layout = Inspect(folder);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogDebug(ex, "Could not inspect {Path}", folder.Path);
            }

            if (layout is not null)
            {
                yield return layout;
            }
        }
    }

    // Every series folder of the Shows and Anime libraries.
    private IEnumerable<SeriesFolder> FindSeriesFolders()
    {
        foreach (var library in _libraryManager.GetVirtualFolders())
        {
            if (PirateFinLibraries.Normalize(library.Name) is not (PirateFinLibraries.Shows or PirateFinLibraries.Anime))
            {
                continue;
            }

            foreach (var location in library.Locations.Where(Directory.Exists))
            {
                foreach (var path in Directory.EnumerateDirectories(location))
                {
                    yield return new SeriesFolder(library.Name, location, library.Locations, path);
                }
            }
        }
    }

    // The series' files and the drives they're on; null when it isn't split. Only series containing a junction (which is
    // how a series gets split) are walked.
    private static SplitLayout? Inspect(SeriesFolder folder)
    {
        var root = new DirectoryInfo(folder.Path);
        if (root.LinkTarget is null && !root.EnumerateDirectories().Any(d => d.LinkTarget is not null))
        {
            return null;
        }

        var layout = new SplitLayout(folder);
        Walk(layout, folder.Path, folder.Path, string.Empty);
        foreach (var file in layout.Files)
        {
            layout.BytesPerDrive[file.Drive] = layout.BytesPerDrive.GetValueOrDefault(file.Drive) + file.Size;
        }

        return layout.BytesPerDrive.Count > 1 ? layout : null;
    }

    // Records the files and folders under a folder, through junctions: where each lives and its path in the series.
    private static void Walk(SplitLayout layout, string entryPath, string physicalPath, string relative)
    {
        var info = new DirectoryInfo(entryPath);
        if (info.LinkTarget is not null && info.ResolveLinkTarget(true) is { } target)
        {
            physicalPath = target.FullName;
            layout.Links.Add(new LinkEntry(entryPath, physicalPath));
        }

        layout.Folders.Add(relative);
        var drive = Path.GetPathRoot(physicalPath)!;
        foreach (var file in new DirectoryInfo(physicalPath).EnumerateFiles())
        {
            layout.Files.Add(new FileEntry(Path.Combine(relative, file.Name), file.FullName, drive, file.Length));
        }

        foreach (var dir in new DirectoryInfo(physicalPath).EnumerateDirectories())
        {
            Walk(layout, dir.FullName, dir.FullName, Path.Combine(relative, dir.Name));
        }
    }

    private void Reunite(SplitLayout split, string target, CancellationToken cancellationToken)
    {
        var folder = split.Folder;
        var staging = Path.Combine(target, LibraryStorage.OverflowFolderName, ".staging", Guid.NewGuid().ToString("N"));
        var homeIsLibraryFolder = string.Equals(Path.GetPathRoot(folder.Location), target, StringComparison.OrdinalIgnoreCase);
        var home = homeIsLibraryFolder
            ? folder.Path
            : Path.Combine(target, LibraryStorage.OverflowFolderName, folder.Library, Path.GetRelativePath(folder.Location, folder.Path));

        _logger.LogInformation("Moving {Series} onto {Drive}", folder.Path, target);
        _libraryMonitor.ReportFileSystemChangeBeginning(folder.Path);
        var moved = new List<(string From, string To)>();
        try
        {
            foreach (var relative in split.Folders)
            {
                Directory.CreateDirectory(Path.Combine(staging, relative));
            }

            // 1. Copy what's on other drives. Nothing is removed yet, so a failure just drops the staging folder.
            foreach (var file in split.Files.Where(f => !IsOn(f, target)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                File.Copy(file.PhysicalPath, Path.Combine(staging, file.RelativePath));
            }

            // 2. What's on the chosen drive already is renamed into the staging folder (instant on the same drive).
            foreach (var file in split.Files.Where(f => IsOn(f, target)))
            {
                var destination = Path.Combine(staging, file.RelativePath);
                File.Move(file.PhysicalPath, destination);
                moved.Add((file.PhysicalPath, destination));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            MoveBack(moved);
            TryDelete(staging);
            _libraryMonitor.ReportFileSystemChangeComplete(folder.Path, false);
            _lastAttempts[folder.Path] = "Failed, retrying next night: " + ex.Message;
            _logger.LogWarning(ex, "Could not move {Series} yet; it stays split", folder.Path);
            if (ex is OperationCanceledException)
            {
                throw;
            }

            return;
        }

        try
        {
            // 3. Swap: the old folders and links go, the staging folder becomes the series' home.
            foreach (var link in split.Links.OrderByDescending(l => l.EntryPath.Length))
            {
                Directory.Delete(link.EntryPath);
            }

            if (Directory.Exists(folder.Path))
            {
                Directory.Delete(folder.Path, true);
            }

            if (homeIsLibraryFolder)
            {
                Directory.Move(staging, folder.Path);
            }
            else
            {
                if (Directory.Exists(home))
                {
                    Directory.Delete(home, true);
                }

                Directory.CreateDirectory(Path.GetDirectoryName(home)!);
                Directory.Move(staging, home);
                LibraryStorage.CreateJunction(folder.Path, home);
            }

            // 4. The old overflow folders only hold the copied originals now.
            foreach (var link in split.Links.Where(l => !IsUnderOrEqual(l.TargetPath, home)))
            {
                TryDelete(link.TargetPath);
                DeleteEmptyParents(link.TargetPath);
            }

            DeleteEmptyParents(staging);

            _lastAttempts[folder.Path] = string.Create(CultureInfo.InvariantCulture, $"Moved onto {target} at {_timeProvider.GetLocalNow():g}");
            _logger.LogInformation("{Series} is on {Drive} now", folder.Path, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // Everything is still on disk (in the staging folder and / or the originals); an admin has to look.
            _lastAttempts[folder.Path] = $"Move interrupted; files are in {staging}: {ex.Message}";
            _logger.LogError(ex, "Moving {Series} was interrupted; its files are in {Staging}", folder.Path, staging);
        }
        finally
        {
            _libraryMonitor.ReportFileSystemChangeComplete(folder.Path, false);
        }
    }

    private static bool IsOn(FileEntry file, string drive) => string.Equals(file.Drive, drive, StringComparison.OrdinalIgnoreCase);

    private static bool IsUnderOrEqual(string path, string folder)
    {
        var relative = Path.GetRelativePath(folder, path);
        return relative == "." || (!relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative));
    }

    private void MoveBack(List<(string From, string To)> moved)
    {
        foreach (var (from, to) in Enumerable.Reverse(moved))
        {
            try
            {
                File.Move(to, from);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogError(ex, "Could not move {File} back to {Original}", to, from);
            }
        }
    }

    private void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not remove {Path}", path);
        }
    }

    // Removes the empty folders left above an overflow or staging folder, up to and including "<drive>\PirateFin Storage".
    private static void DeleteEmptyParents(string path)
    {
        for (var dir = Path.GetDirectoryName(path); dir is not null; dir = Path.GetDirectoryName(dir))
        {
            if (!Directory.Exists(dir) || Directory.EnumerateFileSystemEntries(dir).Any())
            {
                return;
            }

            Directory.Delete(dir);
            if (string.Equals(Path.GetFileName(dir), LibraryStorage.OverflowFolderName, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }
    }

    private sealed record SeriesFolder(string Library, string Location, IReadOnlyList<string> LibraryLocations, string Path);

    private sealed record FileEntry(string RelativePath, string PhysicalPath, string Drive, long Size);

    private sealed record LinkEntry(string EntryPath, string TargetPath);

    private sealed class SplitLayout(SeriesFolder folder)
    {
        public SeriesFolder Folder { get; } = folder;

        public List<string> Folders { get; } = [];

        public List<FileEntry> Files { get; } = [];

        public List<LinkEntry> Links { get; } = [];

        public Dictionary<string, long> BytesPerDrive { get; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
