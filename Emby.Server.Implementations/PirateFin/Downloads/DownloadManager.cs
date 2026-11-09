using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Emby.Server.Implementations.PirateFin.Storage;
using Emby.Server.Implementations.PirateFin.Torrents;
using Jellyfin.Data.Enums;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.PirateFin;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.PirateFin;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Emby.Server.Implementations.PirateFin.Downloads;

/// <summary>
/// Starts episode and movie downloads in qBittorrent and follows them until they are playable and then complete.
/// </summary>
/// <remarks>
/// The downloaded video is renamed (inside qBittorrent) to the placeholder's path, so the item keeps its id and
/// the library scanner never sees a second item for the same episode / movie. Other files in the torrent are skipped.
/// </remarks>
public sealed class DownloadManager : BackgroundService, IDownloadManager
{
    // What qBittorrent reports as the time left when it can't tell (100 days).
    private const long UnknownEtaSeconds = 8640000;

    // A download is slow when, this long after it started, qBittorrent expects it to take longer than _slowEta
    // (qBittorrent reports 8640000 s, 100 days, when it can't tell, e.g. without peers: that counts as slow too).
    private static readonly TimeSpan _slowCheckAfter = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan _slowEta = TimeSpan.FromHours(1);

    // qBittorrent is checked this often while someone follows a download live (PollQuickly), else every
    // MonitorPollSeconds. Downloads waiting for storage and deleted files' torrents are looked at every _housekeeping.
    private static readonly TimeSpan _quickPoll = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan _housekeeping = TimeSpan.FromMinutes(1);

    // qBittorrent says "stalled" whenever no data comes in for a moment; only this long in a row counts.
    private static readonly TimeSpan _stalledAfter = TimeSpan.FromSeconds(30);

    private readonly Channel<Guid> _queue = Channel.CreateUnbounded<Guid>(new UnboundedChannelOptions { SingleReader = true });
    private readonly ConcurrentDictionary<Guid, byte> _queued = new();
    private readonly ConcurrentDictionary<Guid, DownloadMonitor> _monitors = new();

    // Downloads held back because no drive of their library has room; started once one has. Saved across restarts.
    private readonly ConcurrentDictionary<Guid, byte> _waitingForStorage = new();

    // Downloads that failed since the server started (until opened again).
    private readonly ConcurrentDictionary<Guid, byte> _failed = new();

    private readonly QBitTorrentClient _qBitTorrent;
    private readonly ILibraryManager _libraryManager;
    private readonly IProviderManager _providerManager;
    private readonly IFileSystem _fileSystem;
    private readonly IOptionsMonitor<QBitTorrentOptions> _options;
    private readonly LibraryStorage _storage;
    private readonly IApplicationPaths _appPaths;
    private readonly ILogger<DownloadManager> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="DownloadManager"/> class.
    /// </summary>
    /// <param name="qBitTorrent">The qBittorrent client.</param>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="providerManager">The provider manager.</param>
    /// <param name="fileSystem">The file system.</param>
    /// <param name="options">The qBittorrent options.</param>
    /// <param name="storage">Picks the drive downloads go to.</param>
    /// <param name="appPaths">The application paths.</param>
    /// <param name="logger">The logger.</param>
    public DownloadManager(
        QBitTorrentClient qBitTorrent,
        ILibraryManager libraryManager,
        IProviderManager providerManager,
        IFileSystem fileSystem,
        IOptionsMonitor<QBitTorrentOptions> options,
        LibraryStorage storage,
        IApplicationPaths appPaths,
        ILogger<DownloadManager> logger)
    {
        _qBitTorrent = qBitTorrent;
        _libraryManager = libraryManager;
        _providerManager = providerManager;
        _fileSystem = fileSystem;
        _options = options;
        _storage = storage;
        _appPaths = appPaths;
        _logger = logger;
    }

    private string WaitingFile => Path.Combine(_appPaths.DataPath, "piratefin", "waiting-downloads.json");

    /// <inheritdoc />
    public event EventHandler<BaseItem>? DownloadCompleted;

    /// <inheritdoc />
    public event EventHandler<BaseItem>? DownloadPlayable;

    /// <inheritdoc />
    public event EventHandler<Guid>? DownloadUpdated;

    /// <inheritdoc />
    public bool PollQuickly { get; set; }

    /// <inheritdoc />
    public DownloadState GetState(Guid itemId)
    {
        if (_monitors.TryGetValue(itemId, out var monitor))
        {
            return ToState(monitor, DateTime.UtcNow);
        }

        if (_queued.ContainsKey(itemId) || _waitingForStorage.ContainsKey(itemId))
        {
            return DownloadState.Queued;
        }

        return _failed.ContainsKey(itemId) ? DownloadState.Failed : DownloadState.None;
    }

    /// <inheritdoc />
    public bool IsSlow(Guid itemId)
        => _monitors.TryGetValue(itemId, out var monitor)
            && !monitor.IsPlayable
            && DateTime.UtcNow - monitor.StartedUtc >= _slowCheckAfter
            && monitor.EtaSeconds is { } eta
            && eta > _slowEta.TotalSeconds;

    /// <inheritdoc />
    public TimeSpan? GetTimeLeft(Guid itemId)
        => _monitors.TryGetValue(itemId, out var monitor) && monitor.EtaSeconds is { } eta and >= 0 and < UnknownEtaSeconds
            ? TimeSpan.FromSeconds(eta)
            : null;

    /// <inheritdoc />
    public bool QueueDownload(BaseItem item)
    {
        if (item is not (Video and IPirateFinDownloadable downloadable)
            || item.IsDownloaded
            || string.IsNullOrWhiteSpace(downloadable.DownloadUrl)
            || _monitors.ContainsKey(item.Id)
            || !_queued.TryAdd(item.Id, 0))
        {
            return false;
        }

        _failed.TryRemove(item.Id, out _);
        _queue.Writer.TryWrite(item.Id);
        _logger.LogInformation("Queued download of {Name}", Describe(item));
        Updated(item.Id);
        return true;
    }

    /// <inheritdoc />
    public double? GetProgress(Guid itemId)
    {
        if (_monitors.TryGetValue(itemId, out var monitor))
        {
            return monitor.Progress;
        }

        // Queued but not added to qBittorrent yet.
        return _queued.ContainsKey(itemId) ? 0 : null;
    }

    /// <inheritdoc />
    public double? GetContiguousProgress(Guid itemId)
    {
        if (_monitors.TryGetValue(itemId, out var monitor))
        {
            return monitor.ContiguousProgress ?? monitor.Progress;
        }

        return _queued.ContainsKey(itemId) ? 0 : null;
    }

    /// <inheritdoc />
    public IReadOnlyCollection<Guid> GetPendingDownloads()
        => _queued.Keys.Concat(_waitingForStorage.Keys).Concat(_monitors.Keys).ToHashSet();

    /// <inheritdoc />
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
        => Task.WhenAll(ConsumeQueueAsync(stoppingToken), MonitorAsync(stoppingToken));

    private static string Describe(BaseItem item)
        => item is Episode episode ? $"{episode.SeriesName} {episode.Name}" : item.Name;

    /// <summary>
    /// Maps qBittorrent's state to what the Play button shows.
    /// </summary>
    /// <param name="qBitTorrentState">qBittorrent's state (empty before the first check).</param>
    /// <param name="stalledFor">How long it has been stalled in a row; <c>null</c> when it isn't.</param>
    /// <returns>The state.</returns>
    internal static DownloadState ToState(string qBitTorrentState, TimeSpan? stalledFor)
        => qBitTorrentState switch
        {
            "stalledDL" => stalledFor >= _stalledAfter ? DownloadState.Stalled : DownloadState.Downloading,
            "queuedDL" => DownloadState.Queued,
            "pausedDL" or "stoppedDL" => DownloadState.Paused,
            "error" or "missingFiles" => DownloadState.Failed,

            // metaDL, downloading, forced*, checking*, allocating, moving, and done but not moved into the library yet.
            _ => DownloadState.Downloading
        };

    private static DownloadState ToState(DownloadMonitor monitor, DateTime now)
        => ToState(monitor.State, monitor.StalledSinceUtc is { } since ? now - since : null);

    private void Updated(Guid itemId)
    {
        try
        {
            DownloadUpdated?.Invoke(this, itemId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "A download update listener failed");
        }
    }

    // Downloads that were running when the server stopped still have DownloadStartedUtc set; pick them back up.
    private void ResumeInterruptedDownloads()
    {
        LoadWaiting();
        RetryWaitingForStorage();

        var interrupted = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = [BaseItemKind.Episode, BaseItemKind.Movie],
                Recursive = true,
                IsInternet = true
            })
            .Where(i => i is IPirateFinDownloadable d && d.DownloadStartedUtc.HasValue && !i.IsDownloaded && !string.IsNullOrEmpty(d.DownloadUrl));

        foreach (var item in interrupted)
        {
            if (_queued.TryAdd(item.Id, 0))
            {
                _queue.Writer.TryWrite(item.Id);
            }
        }
    }

    private async Task ConsumeQueueAsync(CancellationToken cancellationToken)
    {
        await foreach (var itemId in _queue.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                if (_libraryManager.GetItemById(itemId) is not (Video item and IPirateFinDownloadable downloadable)
                    || item.IsDownloaded
                    || string.IsNullOrWhiteSpace(downloadable.DownloadUrl)
                    || _monitors.ContainsKey(itemId))
                {
                    continue;
                }

                // The item's folder on a drive with room (moved to another drive of the library when its own is full).
                var savePath = _storage.PrepareFolder(item, IsFolderDownloading, cancellationToken);
                if (savePath is null)
                {
                    WaitForStorage(item);
                    Updated(itemId);
                    continue;
                }

                if (_waitingForStorage.TryRemove(itemId, out _))
                {
                    SaveWaiting();
                }

                if (item is Episode episode)
                {
                    await MaterializeSeasonAsync(episode, savePath, cancellationToken).ConfigureAwait(false);
                }

                // An older MovieGenerator created an empty folder at the movie's file path; it would block the rename.
                if (Directory.Exists(item.Path) && !Directory.EnumerateFileSystemEntries(item.Path).Any())
                {
                    Directory.Delete(item.Path);
                }

                var hash = await _qBitTorrent.AddAsync(downloadable.DownloadUrl, savePath, cancellationToken).ConfigureAwait(false);
                _monitors[itemId] = new DownloadMonitor(hash, savePath);

                if (!downloadable.DownloadStartedUtc.HasValue)
                {
                    downloadable.DownloadStartedUtc = DateTime.UtcNow;
                    await item.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
                }

                _logger.LogInformation("Started download of {Name} ({Hash})", Describe(item), hash);
                Updated(itemId);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Failed to start download of {ItemId}", itemId);
            }
            finally
            {
                _queued.TryRemove(itemId, out _);
            }
        }
    }

    private async Task MonitorAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(_quickPoll);
        var resumed = false;
        var lastPoll = DateTime.MinValue;
        var lastHousekeeping = DateTime.UtcNow;
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            // Deferred to the first tick so the library is fully loaded.
            if (!resumed)
            {
                try
                {
                    ResumeInterruptedDownloads();
                    resumed = true;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not resume interrupted downloads yet");
                }
            }

            var now = DateTime.UtcNow;
            if (now - lastHousekeeping >= _housekeeping)
            {
                lastHousekeeping = now;
                RetryWaitingForStorage();
                try
                {
                    await RemoveDeletedTorrentsAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Could not remove the torrents of deleted files from qBittorrent");
                }
            }

            var interval = PollQuickly ? _quickPoll : TimeSpan.FromSeconds(Math.Max(2, _options.CurrentValue.MonitorPollSeconds));

            // A little slack: the timer's ticks aren't exactly 2 s apart.
            if (_monitors.IsEmpty || now - lastPoll < interval - TimeSpan.FromMilliseconds(500))
            {
                continue;
            }

            lastPoll = now;
            await PollAllAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    // One request to qBittorrent for every download followed.
    private async Task PollAllAsync(CancellationToken cancellationToken)
    {
        var monitors = _monitors.ToArray();
        IReadOnlyDictionary<string, TorrentInfo> infos;
        try
        {
            infos = await _qBitTorrent.GetInfosAsync(monitors.Select(m => m.Value.Hash).Distinct(StringComparer.OrdinalIgnoreCase).ToList(), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // qBittorrent may be restarting; keep the monitors and try again next time.
            _logger.LogWarning(ex, "Failed to check the downloads in qBittorrent");
            return;
        }

        foreach (var (itemId, monitor) in monitors)
        {
            try
            {
                await PollAsync(itemId, monitor, infos.GetValueOrDefault(monitor.Hash), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Failed to poll download of {ItemId}", itemId);
            }
            finally
            {
                Updated(itemId);
            }
        }
    }

    private async Task PollAsync(Guid itemId, DownloadMonitor monitor, TorrentInfo? info, CancellationToken cancellationToken)
    {
        if (_libraryManager.GetItemById(itemId) is not (Video item and IPirateFinDownloadable downloadable))
        {
            // Deleted while downloading (itself, or its season / series): the unfinished download goes too.
            _monitors.TryRemove(itemId, out _);
            if (!_monitors.Values.Any(m => string.Equals(m.Hash, monitor.Hash, StringComparison.OrdinalIgnoreCase)))
            {
                _logger.LogInformation("Removing the download of deleted item {ItemId} ({Hash}) from qBittorrent", itemId, monitor.Hash);
                await _qBitTorrent.DeleteAsync(monitor.Hash, true, cancellationToken).ConfigureAwait(false);
            }

            return;
        }

        if (info is null || info.IsFailed)
        {
            _logger.LogWarning("Download of {Name} stopped ({State}); it can be restarted by opening it", Describe(item), info?.State ?? "removed from qBittorrent");
            _monitors.TryRemove(itemId, out _);
            _failed[itemId] = 0;
            downloadable.DownloadStartedUtc = null;
            await item.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
            return;
        }

        monitor.Progress = Math.Clamp(info.Progress, 0, 1);
        monitor.EtaSeconds = info.EtaSeconds;
        if (info.State != "stalledDL")
        {
            monitor.StalledSinceUtc = null;
        }
        else if (monitor.State != "stalledDL" || !monitor.StalledSinceUtc.HasValue)
        {
            monitor.StalledSinceUtc = DateTime.UtcNow;
        }

        monitor.State = info.State;

        if (!monitor.FileNamed)
        {
            monitor.FileNamed = await NameFileAsync(item, monitor, cancellationToken).ConfigureAwait(false);
        }

        await UpdateContiguousProgressAsync(monitor, info, cancellationToken).ConfigureAwait(false);

        // Playable once the start of the video is there without a gap, not when that much of it is there in total:
        // a player reaching a piece that hasn't arrived reads zeros (the picture freezes while the sound goes on).
        if (!monitor.IsPlayable && monitor.FileNamed && (monitor.ContiguousProgress ?? info.Progress) >= _options.CurrentValue.PlayableProgress)
        {
            monitor.IsPlayable = await MakePlayableAsync(item, cancellationToken).ConfigureAwait(false);
            if (monitor.IsPlayable)
            {
                DownloadPlayable?.Invoke(this, item);
            }
        }

        if (info.IsComplete && monitor.FileNamed)
        {
            await CompleteAsync(item, downloadable, monitor.Hash, cancellationToken).ConfigureAwait(false);
            _monitors.TryRemove(itemId, out _);
        }
    }

    // Once the magnet's metadata has arrived: rename the main video to the placeholder's file name and skip everything
    // else in the torrent (samples, .nfo / .txt files, posters).
    private async Task<bool> NameFileAsync(Video item, DownloadMonitor monitor, CancellationToken cancellationToken)
    {
        var hash = monitor.Hash;
        var files = await _qBitTorrent.GetFilesAsync(hash, cancellationToken).ConfigureAwait(false);
        var video = QBitTorrentClient.GetPrimaryVideo(files);
        if (video is null)
        {
            // Metadata not downloaded yet.
            return false;
        }

        monitor.FirstPiece = video.FirstPiece;
        monitor.LastPiece = video.LastPiece;
        await _qBitTorrent.SetFilePriorityAsync(hash, files.Where(f => f.Index != video.Index).Select(f => f.Index), 0, cancellationToken).ConfigureAwait(false);

        var targetName = Path.GetFileName(item.Path);
        if (string.Equals(video.Name, targetName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (await _qBitTorrent.RenameFileAsync(hash, video.Name, targetName, cancellationToken).ConfigureAwait(false))
        {
            _logger.LogInformation("Renamed {Original} to {Target}", video.Name, targetName);
            return true;
        }

        // Rename refused (e.g. odd multi-file layout): point the item at the file where it is. The id is kept.
        item.Path = Path.Combine(item.ContainingFolderPath, video.Name.Replace('/', Path.DirectorySeparatorChar));
        await item.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
        return true;
    }

    // How much of the video is there without a gap from its start (null: unknown, e.g. qBittorrent too old to say
    // which pieces the file has; the overall progress is used then).
    private async Task UpdateContiguousProgressAsync(DownloadMonitor monitor, TorrentInfo info, CancellationToken cancellationToken)
    {
        if (info.IsComplete)
        {
            monitor.ContiguousProgress = 1;
            return;
        }

        if (monitor.FirstPiece is not { } first || monitor.LastPiece is not { } last)
        {
            return;
        }

        try
        {
            var states = await _qBitTorrent.GetPieceStatesAsync(monitor.Hash, cancellationToken).ConfigureAwait(false);
            monitor.ContiguousProgress = QBitTorrentClient.GetContiguousProgress(states, first, last);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Keep the last value; tried again on the next poll.
            _logger.LogDebug(ex, "Could not read the pieces of {Hash}", monitor.Hash);
        }
    }

    private async Task<bool> MakePlayableAsync(Video item, CancellationToken cancellationToken)
    {
        var file = _fileSystem.GetFileSystemInfo(item.Path);
        if (!file.Exists || file.IsDirectory || !await ProbeAsync(item, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        item.IsPlayable = item.RunTimeTicks > 0;
        item.IsPlaceHolder = false;
        item.IsVirtualItem = false;
        item.VideoType = VideoType.VideoFile;
        item.DateModified = file.LastWriteTimeUtc;
        await item.UpdateToRepositoryAsync(ItemUpdateType.MetadataImport, cancellationToken).ConfigureAwait(false);
        await _providerManager.RefreshItem(item, CreateFullRefreshOptions(), cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("{Name} is now playable", Describe(item));
        return true;
    }

    private async Task CompleteAsync(Video item, IPirateFinDownloadable downloadable, string hash, CancellationToken cancellationToken)
    {
        await _qBitTorrent.StopAsync(hash, cancellationToken).ConfigureAwait(false);
        await ProbeAsync(item, cancellationToken).ConfigureAwait(false);

        item.IsDownloaded = true;
        item.IsInternet = false;
        item.IsPlayable = true;
        item.IsPlaceHolder = false;
        item.IsVirtualItem = false;
        item.Scheduled = false;
        item.VideoType = VideoType.VideoFile;
        downloadable.DownloadStartedUtc = null;
        await item.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);

        try
        {
            // Picks up the final media streams and any external subtitles next to the file.
            await item.RefreshMetadata(CreateFullRefreshOptions(), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to refresh streams for {Name} after download", Describe(item));
        }

        _logger.LogInformation("Finished downloading {Name}", Describe(item));
        DownloadCompleted?.Invoke(this, item);
    }

    // Reads the runtime with ffprobe; fails while qBittorrent still holds an exclusive lock on the file.
    private async Task<bool> ProbeAsync(Video item, CancellationToken cancellationToken)
    {
        try
        {
            await item.RefreshMetadata(
                new MetadataRefreshOptions(new DirectoryService(_fileSystem))
                {
                    MetadataRefreshMode = MetadataRefreshMode.ValidationOnly,
                    ImageRefreshMode = MetadataRefreshMode.None,
                    ForceSave = true,
                    IsAutomated = true
                },
                cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Could not probe {Name} yet", Describe(item));
            return false;
        }
    }

    private MetadataRefreshOptions CreateFullRefreshOptions() => new(new DirectoryService(_fileSystem))
    {
        MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
        ImageRefreshMode = MetadataRefreshMode.FullRefresh,
        ReplaceAllMetadata = true,
        ReplaceAllImages = true,
        ForceSave = true,
        RemoveOldMetadata = true,
        RegenerateTrickplay = true,
        EnableRemoteContentProbe = true
    };

    private async Task MaterializeSeasonAsync(Episode episode, string seasonPath, CancellationToken cancellationToken)
    {
        var season = episode.Season;
        if (season is null || (!season.IsVirtualItem && string.Equals(season.Path, seasonPath, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        season.Path = seasonPath;
        season.IsVirtualItem = false;
        await season.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public bool IsDownloadingUnder(string folder)
    {
        var prefix = Path.TrimEndingDirectorySeparator(folder) + Path.DirectorySeparatorChar;
        return _monitors.Values.Any(m => IsFolderOrUnder(m.Folder, folder, prefix));

        static bool IsFolderOrUnder(string path, string folder, string prefix)
            => string.Equals(Path.TrimEndingDirectorySeparator(path), Path.TrimEndingDirectorySeparator(folder), StringComparison.OrdinalIgnoreCase)
                || path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Removes the finished torrents in the libraries' folders whose file is gone (deleted in PirateFin, with its
    /// season / series, or by hand), so they don't sit in qBittorrent as "missing files" and block a new download of
    /// the same release. Files are never deleted here. Torrents outside the libraries are left alone.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    internal async Task RemoveDeletedTorrentsAsync(CancellationToken cancellationToken)
    {
        var libraryFolders = _libraryManager.GetVirtualFolders()
            .SelectMany(f => f.Locations)
            .Where(l => !string.IsNullOrEmpty(l))
            .Select(l => Path.TrimEndingDirectorySeparator(l) + Path.DirectorySeparatorChar)
            .ToList();
        var downloading = _monitors.Values.Select(m => m.Hash).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var torrent in await _qBitTorrent.GetAllAsync(cancellationToken).ConfigureAwait(false))
        {
            if ((!torrent.IsComplete && !torrent.IsFailed)
                || downloading.Contains(torrent.Hash)
                || string.IsNullOrEmpty(torrent.ContentPath)
                || !libraryFolders.Any(f => torrent.ContentPath.StartsWith(f, StringComparison.OrdinalIgnoreCase))
                || File.Exists(torrent.ContentPath)
                || Directory.Exists(torrent.ContentPath))
            {
                continue;
            }

            _logger.LogInformation("Removing torrent {Hash} from qBittorrent: {Path} was deleted", torrent.Hash, torrent.ContentPath);
            await _qBitTorrent.DeleteAsync(torrent.Hash, false, cancellationToken).ConfigureAwait(false);
        }
    }

    private bool IsFolderDownloading(string folder)
        => _monitors.Values.Any(m => string.Equals(m.Folder, folder, StringComparison.OrdinalIgnoreCase));

    private void WaitForStorage(BaseItem item)
    {
        if (_waitingForStorage.TryAdd(item.Id, 0))
        {
            _logger.LogWarning("Library storage is full; {Name} will download once a drive has room", Describe(item));
            SaveWaiting();
        }
    }

    // Requeues the held back downloads whose library has room again.
    private void RetryWaitingForStorage()
    {
        foreach (var itemId in _waitingForStorage.Keys)
        {
            var item = _libraryManager.GetItemById(itemId);
            if (item is null || item.IsDownloaded)
            {
                _waitingForStorage.TryRemove(itemId, out _);
                SaveWaiting();
            }
            else if (_storage.CanStore(item))
            {
                QueueDownload(item);
            }
        }
    }

    private void LoadWaiting()
    {
        try
        {
            if (File.Exists(WaitingFile))
            {
                foreach (var id in JsonSerializer.Deserialize<Guid[]>(File.ReadAllText(WaitingFile)) ?? [])
                {
                    _waitingForStorage.TryAdd(id, 0);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            _logger.LogWarning(ex, "Could not read the downloads waiting for storage");
        }
    }

    private void SaveWaiting()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(WaitingFile)!);
            File.WriteAllText(WaitingFile, JsonSerializer.Serialize(_waitingForStorage.Keys.ToArray()));
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Could not save the downloads waiting for storage");
        }
    }

    private sealed class DownloadMonitor(string hash, string folder)
    {
        public string Hash { get; } = hash;

        public string Folder { get; } = folder;

        public bool FileNamed { get; set; }

        public bool IsPlayable { get; set; }

        public double Progress { get; set; }

        // The video's pieces in the torrent, once its metadata is in.
        public int? FirstPiece { get; set; }

        public int? LastPiece { get; set; }

        // How much of the video is downloaded without a gap from its start; null when unknown.
        public double? ContiguousProgress { get; set; }

        public DateTime StartedUtc { get; } = DateTime.UtcNow;

        public long? EtaSeconds { get; set; }

        // qBittorrent's state at the last check (empty before it).
        public string State { get; set; } = string.Empty;

        public DateTime? StalledSinceUtc { get; set; }
    }
}
