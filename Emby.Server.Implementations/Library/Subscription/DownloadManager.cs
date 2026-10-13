using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

public sealed class DownloadManager : BackgroundService, IDownloadManager
{
    private readonly QBitTorrentManager _qBitTorrentManager;
    private readonly ILibraryManager _libraryManager;
    private readonly IProviderManager _providerManager;
    private readonly ILogger<DownloadManager> _logger;
    private readonly QBitTorrentOptions _options;
    private readonly IFileSystem _fileSystem;

    private readonly Channel<Guid> _queue = Channel.CreateUnbounded<Guid>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    private readonly ConcurrentDictionary<Guid, byte> _queued = new();
    private readonly ConcurrentDictionary<Guid, TorrentMonitorState> _activeMonitors = new();

    public DownloadManager(
        QBitTorrentManager qBitTorrentManager,
        ILibraryManager libraryManager,
        IOptions<QBitTorrentOptions> options,
        IFileSystem fileSystem,
        ILogger<DownloadManager> logger,
        IProviderManager providerManager)
    {
        _qBitTorrentManager = qBitTorrentManager;
        _libraryManager = libraryManager;
        _logger = logger;
        _options = options.Value;
        _fileSystem = fileSystem;
        _providerManager = providerManager;
    }

    public Task QueueDownload(Episode episode, CancellationToken cancellationToken = default)
    {
        if (episode.IsDownloaded || _activeMonitors.ContainsKey(episode.Id))
        {
            return Task.CompletedTask;
        }

        if (!_queued.TryAdd(episode.Id, 0))
        {
            return Task.CompletedTask;
        }

        _queue.Writer.TryWrite(episode.Id);
        _logger.LogInformation("Queued episode {EpisodeId} for download", episode.Name);
        return Task.CompletedTask;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Download manager started.");

        var consumeTask = ConsumeQueueAsync(stoppingToken);
        var monitorTask = MonitorActiveDownloadsAsync(stoppingToken);

        await Task.WhenAll(consumeTask, monitorTask).ConfigureAwait(false);
    }

    private async Task ConsumeQueueAsync(CancellationToken ct)
    {
        await foreach (var episodeId in _queue.Reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            try
            {
                var episode = _libraryManager.GetItemById(episodeId) as Episode;
                if (episode is null)
                {
                    continue;
                }

                if (episode.IsDownloaded)
                {
                    continue;
                }

                if (_activeMonitors.ContainsKey(episode.Id))
                {
                    _logger.LogDebug("Episode {EpisodeId} is already being monitored; skipping duplicate start", episode.Name);
                    continue;
                }

                if (string.IsNullOrWhiteSpace(episode.DownloadUrl))
                {
                    _logger.LogWarning("Skipping episode {EpisodeId} because DownloadUrl is empty", episode.Name);
                    continue;
                }

                var savePath = episode.ContainingFolderPath;
                if (string.IsNullOrWhiteSpace(savePath))
                {
                    _logger.LogWarning("Skipping episode {EpisodeId} because save path could not be resolved", episode.Name);
                    continue;
                }

                if (!Directory.Exists(savePath))
                {
                    Directory.CreateDirectory(savePath);
                    _logger.LogInformation("Created missing download path {SavePath} for episode {EpisodeId}", savePath, episode.Name);
                    await MaterializeSeasonAfterDirectoryCreationAsync(episode, savePath, ct).ConfigureAwait(false);
                }

                var hash = await _qBitTorrentManager.StartTorrentAsync(episode.DownloadUrl, episode.Id, savePath, ct).ConfigureAwait(false);

                // qBittorrent is asked to save the file under episode.Path's name directly (see rename param), so the
                // post-download move below is only a fallback for torrents that don't honor it (e.g. multi-file torrents).
                _activeMonitors[episode.Id] = new TorrentMonitorState(
                    episode.Id,
                    hash,
                    episode.Name ?? episode.SeriesName,
                    false,
                    DateTimeOffset.UtcNow);
                _logger.LogInformation("Started download for episode {EpisodeId} with torrent hash {Hash}", episode.Name, hash);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to queue/start episode download {EpisodeId}", episodeId);
            }
            finally
            {
                _queued.TryRemove(episodeId, out _);
            }
        }
    }

    private async Task MonitorActiveDownloadsAsync(CancellationToken ct)
    {
        var intervalSeconds = Math.Max(2, _options.MonitorPollSeconds);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(intervalSeconds));

        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            var monitors = _activeMonitors.Values.ToList();
            if (monitors.Count == 0)
            {
                continue;
            }

            foreach (var monitor in monitors)
            {
                var currentMonitor = monitor;
                try
                {
                    var info = await _qBitTorrentManager.GetTorrentInfoAsync(currentMonitor.Hash, ct).ConfigureAwait(false);
                    if (info is null)
                    {
                        continue;
                    }

                    var monitoredEpisode = _libraryManager.GetItemById(currentMonitor.EpisodeId) as Episode;
                    if (monitoredEpisode is not null && !monitoredEpisode.IsDownloaded && !TryGetReadyEpisodeFile(monitoredEpisode, out _))
                    {
                        await TryMoveTorrentOutputToCanonicalPathAsync(monitoredEpisode, currentMonitor.Hash, ct).ConfigureAwait(false);
                    }

                    var now = DateTimeOffset.UtcNow;
                    _logger.LogInformation("Polling Episode {EpisodeId}, currently at {ProgressPercent}%.", currentMonitor.EpisodeId, info.Progress * 100);
                    if (currentMonitor.IsPlayableReady != true && info.Progress >= 0.10)
                    {
                        var playableEpisode = _libraryManager.GetItemById(currentMonitor.EpisodeId) as Episode;
                        if (playableEpisode is not null)
                        {
                            if (!TryGetReadyEpisodeFile(playableEpisode, out var fileMeta))
                            {
                                _logger.LogDebug(
                                    "Episode {EpisodeId} reached 10% but media file is not ready at path {EpisodePath}",
                                    playableEpisode.Id,
                                    playableEpisode.Path);
                                continue;
                            }

                            if (!await ProcessRuntimeTicksAsync(playableEpisode, ct).ConfigureAwait(false))
                            {
                                _logger.LogDebug(
                                    "Episode {EpisodeId} reached 10% but media file is still locked by the download client; will retry probing later",
                                    playableEpisode.Id);
                                continue;
                            }

                            playableEpisode.IsPlayable = playableEpisode.RunTimeTicks.HasValue && playableEpisode.RunTimeTicks.Value > 0;
                            playableEpisode.IsPlaceHolder = false;
                            playableEpisode.IsVirtualItem = false;
                            if (monitoredEpisode is not null)
                            {
                                string torrentFilePath = await TryGetTorrentFileName(monitoredEpisode, currentMonitor.Hash, ct).ConfigureAwait(false);
                                if (!string.IsNullOrWhiteSpace(torrentFilePath))
                                {
                                    playableEpisode.Path = torrentFilePath;
                                }

                                playableEpisode.VideoType = VideoType.VideoFile;

                                if (fileMeta is not null)
                                {
                                    playableEpisode.DateModified = fileMeta.LastWriteTimeUtc;
                                    if (playableEpisode.DateCreated == default)
                                    {
                                        playableEpisode.DateCreated = fileMeta.CreationTimeUtc;
                                    }
                                }

                                await playableEpisode.UpdateToRepositoryAsync(ItemUpdateType.MetadataImport, ct).ConfigureAwait(false);
                                await _providerManager.RefreshItem(playableEpisode, CreateStreamAndSubtitleRefreshOptions(), ct).ConfigureAwait(false);

                                currentMonitor = currentMonitor with
                                {
                                    IsPlayableReady = true,
                                    NextCompletionCheckUtc = now.AddMinutes(5)
                                };
                                _activeMonitors[currentMonitor.EpisodeId] = currentMonitor;
                                _logger.LogInformation("Episode {EpisodeId} reached 10% download progress and is now playable", currentMonitor.EpisodeId);
                            }
                        }
                    }

                    // if (currentMonitor.IsPlayableReady && now < currentMonitor.NextCompletionCheckUtc)
                    // {
                    //     continue;
                    // }

                    if (info.IsComplete)
                    {
                        var episode = _libraryManager.GetItemById(currentMonitor.EpisodeId) as Episode;

                        _logger.LogInformation("Pausing Episode {EpisodeId} due to completed download", currentMonitor.EpisodeId);
                        await _qBitTorrentManager.StopTorrentAsync(currentMonitor.Hash, ct).ConfigureAwait(false);

                        if (episode is not null)
                        {
                            await ProcessRuntimeTicksAsync(episode, ct).ConfigureAwait(false);
                            episode.IsDownloaded = true;
                            episode.IsInternet = false;
                            episode.IsPlayable = true;
                            episode.IsPlaceHolder = false;
                            await episode.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, ct).ConfigureAwait(false);
                            await RefreshMediaStreamsAndSubtitlesAsync(episode, ct).ConfigureAwait(false);
                            var progressObj = new Progress<double>(_ => { /* optionally log progress */ });
                            await _libraryManager.ValidateMediaLibrary(progressObj, CancellationToken.None).ConfigureAwait(false);
                        }

                        _activeMonitors.TryRemove(currentMonitor.EpisodeId, out _);
                        continue;
                    }

                    if (currentMonitor.IsPlayableReady)
                    {
                        _activeMonitors[currentMonitor.EpisodeId] = currentMonitor with
                        {
                            NextCompletionCheckUtc = now.AddMinutes(5)
                        };
                    }

                    if (info.IsFailed)
                    {
                        _activeMonitors.TryRemove(currentMonitor.EpisodeId, out _);
                        _logger.LogWarning("Episode {EpisodeId} download failed with state {State}", currentMonitor.EpisodeId, info.State);
                    }
                }
                catch (Exception ex)
                {
                    _activeMonitors.TryRemove(currentMonitor.EpisodeId, out _);
                    _logger.LogWarning(ex, "Failed monitoring episode {EpisodeId}", currentMonitor.EpisodeId);
                }
            }
        }
    }

    private static string ResolveShowsSavePath(Episode episode)
    {
        var collectionFolderPath = episode.FindParent<CollectionFolder>()?.Path;
        if (!string.IsNullOrWhiteSpace(collectionFolderPath))
        {
            return collectionFolderPath;
        }

        var seriesPath = episode.Series?.Path;
        if (!string.IsNullOrWhiteSpace(seriesPath))
        {
            return seriesPath;
        }

        return Path.GetDirectoryName(episode.Path) ?? string.Empty;
    }

    // Moves the torrent's actual output file onto episode.Path (set once at placeholder creation and never mutated)
    // so BaseItem.Id (a hash of Path+Type) stays identical, preventing the scanner from resolving a duplicate item.
    private async Task TryMoveTorrentOutputToCanonicalPathAsync(Episode episode, string hash, CancellationToken ct)
    {
        var savePath = episode.ContainingFolderPath;
        var canonicalPath = episode.Path;
        if (string.IsNullOrWhiteSpace(savePath) || string.IsNullOrWhiteSpace(canonicalPath))
        {
            return;
        }

        var resolvedPath = await _qBitTorrentManager.ResolvePrimaryVideoPathAsync(hash, savePath, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(resolvedPath) || !File.Exists(resolvedPath))
        {
            return;
        }

        if (string.Equals(resolvedPath, canonicalPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            File.Move(resolvedPath, canonicalPath, overwrite: true);

            var nestedDirectory = Path.GetDirectoryName(resolvedPath);
            if (!string.IsNullOrEmpty(nestedDirectory)
                && !string.Equals(nestedDirectory, savePath, StringComparison.OrdinalIgnoreCase)
                && Directory.Exists(nestedDirectory)
                && Directory.GetFileSystemEntries(nestedDirectory).Length == 0)
            {
                Directory.Delete(nestedDirectory);
            }

            _logger.LogInformation("Moved torrent output for episode {EpisodeId} onto canonical path {Path}; Id unchanged", episode.Id, canonicalPath);
        }
        catch (IOException ex)
        {
            // qBittorrent may still hold the file open (e.g. exclusive lock on Windows); retry on the next poll tick.
            _logger.LogDebug(ex, "Torrent output for episode {EpisodeId} not ready to move into place yet, will retry", episode.Id);
        }
    }

    private async Task<string> TryGetTorrentFileName(Episode episode, string hash, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(hash))
        {
            return string.Empty;
        }

        var savePath = episode.ContainingFolderPath;
        if (string.IsNullOrWhiteSpace(savePath))
        {
            return string.Empty;
        }

        try
        {
            string resolvedPath = await _qBitTorrentManager.ResolvePrimaryVideoPathAsync(hash, savePath, ct).ConfigureAwait(false);
            return resolvedPath;
        }
        catch (IOException ex)
        {
            // qBittorrent may still hold the file open (e.g. exclusive lock on Windows); retry on the next poll tick.
            _logger.LogDebug(ex, "Torrent output for episode {EpisodeId} not ready to move into place yet, will retry", episode.Id);
        }

        return string.Empty;
    }

    private async Task<bool> ProcessRuntimeTicksAsync(Episode episode, CancellationToken ct)
    {
        if (!TryGetReadyEpisodeFile(episode, out _))
        {
            return false;
        }

        var options = new MetadataRefreshOptions(new DirectoryService(_fileSystem))
        {
            MetadataRefreshMode = MetadataRefreshMode.ValidationOnly,
            ImageRefreshMode = MetadataRefreshMode.None,
            ForceSave = true,
            IsAutomated = true
        };

        try
        {
            await episode.RefreshMetadata(options, ct).ConfigureAwait(false);
            await episode.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, ct).ConfigureAwait(false);
            return true;
        }
        catch (IOException ex)
        {
            // qBittorrent still holds the file open for writing; retry probing on a later poll once it's released.
            _logger.LogDebug(ex, "Media file for episode {EpisodeId} is still locked by the download client", episode.Id);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Runtime metadata processing did not complete yet for episode {EpisodeId}", episode.Id);
            return false;
        }
    }

    private MetadataRefreshOptions CreateStreamAndSubtitleRefreshOptions()
    {
        return new MetadataRefreshOptions(new DirectoryService(_fileSystem))
        {
            MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
            ImageRefreshMode = MetadataRefreshMode.FullRefresh,
            ReplaceAllMetadata = true,
            ReplaceAllImages = true,
            ForceSave = true,
            IsAutomated = false,
            RemoveOldMetadata = true,
            RegenerateTrickplay = true,
            EnableRemoteContentProbe = true
        };
    }

    private async Task RefreshMediaStreamsAndSubtitlesAsync(Episode episode, CancellationToken ct)
    {
        if (!TryGetReadyEpisodeFile(episode, out _))
        {
            return;
        }

        try
        {
            await episode.RefreshMetadata(CreateStreamAndSubtitleRefreshOptions(), ct).ConfigureAwait(false);
            await episode.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, ct).ConfigureAwait(false);
            _logger.LogInformation("Refreshed media streams and subtitles for episode {EpisodeId} after download completion", episode.Id);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to refresh media streams/subtitles for episode {EpisodeId}", episode.Id);
        }
    }

    private async Task MaterializeSeasonAfterDirectoryCreationAsync(Episode episode, string seasonPath, CancellationToken ct)
    {
        var season = episode.Season;
        if (season is null)
        {
            return;
        }

        if (!season.IsVirtualItem && string.Equals(season.Path, seasonPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        season.Path = seasonPath;
        season.IsVirtualItem = false;
        await season.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, ct).ConfigureAwait(false);
        _logger.LogInformation("Materialized season {SeasonId} at {SeasonPath} after download folder creation", season.Id, seasonPath);
    }

    private bool TryGetReadyEpisodeFile(Episode episode, out FileSystemMetadata? fileMeta)
    {
        fileMeta = null;
        if (string.IsNullOrWhiteSpace(episode.Path))
        {
            return false;
        }

        var metadata = _fileSystem.GetFileSystemInfo(episode.Path);
        if (metadata is null)
        {
            return false;
        }

        fileMeta = metadata;
        return true;
    }

    private sealed record TorrentMonitorState(
        Guid EpisodeId,
        string Hash,
        string Name,
        bool IsPlayableReady,
        DateTimeOffset NextCompletionCheckUtc);
}
