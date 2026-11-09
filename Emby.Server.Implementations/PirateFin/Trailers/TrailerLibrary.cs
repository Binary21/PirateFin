using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.PirateFin;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.PirateFin;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Emby.Server.Implementations.PirateFin.Trailers;

/// <summary>
/// <see cref="ITrailerLibrary"/>: downloads with yt-dlp and Jellyfin's ffmpeg, one at a time, into
/// &lt;title folder&gt;/trailers/trailer.mp4, then refreshes the title so Jellyfin lists the trailer. Which trailers it
/// saved, and which couldn't be saved (gone or below 720p: retried after 30 days; other failures: after 6 hours), is
/// kept in &lt;data&gt;/piratefin/saved-trailers.json. A movie gets its trailer when it finishes downloading, a series
/// when an episode does; the "Save Trailers" scheduled task catches up on the rest.
/// </summary>
public sealed partial class TrailerLibrary : BackgroundService, ITrailerLibrary
{
    /// <summary>
    /// How much of a trailer is saved.
    /// </summary>
    public const int Seconds = 30;

    /// <summary>
    /// The folder, inside the title's folder, that Jellyfin reads trailers from.
    /// </summary>
    public const string TrailersFolder = "trailers";

    /// <summary>
    /// The saved trailer's file name.
    /// </summary>
    public const string TrailerFile = "trailer.mp4";

    private static readonly TimeSpan _retryUnavailable = TimeSpan.FromDays(30);
    private static readonly TimeSpan _retryFailed = TimeSpan.FromHours(6);
    private static readonly TimeSpan _downloadTimeout = TimeSpan.FromMinutes(3);
    private static readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };
    private static readonly string[] _libraries = [PirateFinLibraries.Movies, PirateFinLibraries.Shows, PirateFinLibraries.Anime];

    // 720p-1080p H.264 + AAC, which every TV plays; yt-dlp cuts the first 30 seconds with ffmpeg.
    private static readonly string _format = "bv*[vcodec^=avc1][height>=720][height<=1080]+ba[acodec^=mp4a]/b[vcodec^=avc1][height>=720][height<=1080]";

    private readonly Channel<Guid> _queue = Channel.CreateUnbounded<Guid>(new UnboundedChannelOptions { SingleReader = true });
    private readonly SemaphoreSlim _downloadLock = new(1, 1);
    private readonly Lock _lock = new();
    private readonly YtDlp _ytDlp;
    private readonly IMediaEncoder _mediaEncoder;
    private readonly ILibraryManager _libraryManager;
    private readonly IProviderManager _providerManager;
    private readonly IFileSystem _fileSystem;
    private readonly ILibraryStorage _storage;
    private readonly IDownloadManager _downloadManager;
    private readonly IOptionsMonitor<TrailerOptions> _options;
    private readonly IApplicationPaths _appPaths;
    private readonly ILogger<TrailerLibrary> _logger;
    private Records? _records;

    /// <summary>
    /// Initializes a new instance of the <see cref="TrailerLibrary"/> class.
    /// </summary>
    /// <param name="ytDlp">The yt-dlp tool.</param>
    /// <param name="mediaEncoder">The media encoder, for ffmpeg.</param>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="providerManager">The provider manager, to refresh titles.</param>
    /// <param name="fileSystem">The file system.</param>
    /// <param name="storage">The library storage, for the minimum free space.</param>
    /// <param name="downloadManager">The download manager, for finished downloads.</param>
    /// <param name="options">The trailer options.</param>
    /// <param name="appPaths">The application paths.</param>
    /// <param name="logger">The logger.</param>
    public TrailerLibrary(
        YtDlp ytDlp,
        IMediaEncoder mediaEncoder,
        ILibraryManager libraryManager,
        IProviderManager providerManager,
        IFileSystem fileSystem,
        ILibraryStorage storage,
        IDownloadManager downloadManager,
        IOptionsMonitor<TrailerOptions> options,
        IApplicationPaths appPaths,
        ILogger<TrailerLibrary> logger)
    {
        _ytDlp = ytDlp;
        _mediaEncoder = mediaEncoder;
        _libraryManager = libraryManager;
        _providerManager = providerManager;
        _fileSystem = fileSystem;
        _storage = storage;
        _downloadManager = downloadManager;
        _options = options;
        _appPaths = appPaths;
        _logger = logger;
    }

    private string DataFolder => Path.Combine(_appPaths.DataPath, "piratefin");

    private string WorkFolder => Path.Combine(DataFolder, "trailer-downloads");

    private string RecordsPath => Path.Combine(DataFolder, "saved-trailers.json");

    /// <summary>
    /// Gets the YouTube video id of a trailer url (watch?v=, youtu.be/, embed/ or shorts/ links).
    /// </summary>
    /// <param name="url">The url.</param>
    /// <returns>The id, or <c>null</c>.</returns>
    public static string? GetYouTubeId(string? url)
    {
        var match = url is null ? null : YouTubeIdRegex().Match(url);
        return match is { Success: true } ? match.Groups[1].Value : null;
    }

    /// <summary>
    /// Tells whether yt-dlp's output means the trailer will never be saved (as opposed to a passing failure).
    /// </summary>
    /// <param name="output">What yt-dlp printed.</param>
    /// <returns><c>true</c> when the video is gone, private, age restricted or has no 720p+ H.264 format.</returns>
    internal static bool IsPermanentFailure(string output)
        => output.Contains("Requested format is not available", StringComparison.OrdinalIgnoreCase)
            || output.Contains("video is unavailable", StringComparison.OrdinalIgnoreCase)
            || output.Contains("Private video", StringComparison.OrdinalIgnoreCase)
            || output.Contains("has been removed", StringComparison.OrdinalIgnoreCase)
            || output.Contains("Sign in to confirm your age", StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc />
    public void Enqueue(BaseItem item)
    {
        var title = item is Episode episode ? episode.Series : item;
        if (title is Movie or Series && _options.CurrentValue.Enabled)
        {
            _queue.Writer.TryWrite(title.Id);
        }
    }

    /// <inheritdoc />
    public async Task<TrailerSaveResult> SaveAsync(BaseItem item, CancellationToken cancellationToken)
    {
        var folder = GetTitleFolder(item);
        var videoId = item.RemoteTrailers?.Select(t => GetYouTubeId(t.Url)).FirstOrDefault(id => id is not null);
        if (!_options.CurrentValue.Enabled || folder is null || videoId is null || HasTrailer(item, folder) || IsRecentlyAttempted(item.Id, videoId))
        {
            return TrailerSaveResult.Skipped;
        }

        if (!_storage.HasRoomAt(folder))
        {
            _logger.LogInformation("Not saving the trailer of {Title}: its drive is below the minimum free space", item.Name);
            return TrailerSaveResult.NoRoom;
        }

        await _downloadLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Checked again: the same title may have been queued twice.
            if (HasTrailer(item, folder))
            {
                return TrailerSaveResult.Skipped;
            }

            var result = await DownloadAsync(item, videoId, folder, cancellationToken).ConfigureAwait(false);
            if (result == TrailerSaveResult.Saved)
            {
                Refresh(item);
            }

            return result;
        }
        finally
        {
            _downloadLock.Release();
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<BaseItem> GetMissing()
    {
        var titles = new List<BaseItem>();
        foreach (var library in _libraries)
        {
            var folder = _libraryManager.GetVirtualFolders().FirstOrDefault(vf => vf.Name.Equals(library, StringComparison.OrdinalIgnoreCase));
            if (folder is null || !Guid.TryParse(folder.ItemId, out var folderId))
            {
                continue;
            }

            titles.AddRange(_libraryManager.GetItemList(new InternalItemsQuery
            {
                ParentId = folderId,
                Recursive = true,
                IncludeItemTypes = [library == PirateFinLibraries.Movies ? BaseItemKind.Movie : BaseItemKind.Series]
            }));
        }

        return titles
            .Where(title => GetTitleFolder(title) is { } path
                && title.RemoteTrailers?.Select(t => GetYouTubeId(t.Url)).FirstOrDefault(id => id is not null) is { } videoId
                && !HasTrailer(title, path)
                && !IsRecentlyAttempted(title.Id, videoId))
            .ToList();
    }

    /// <inheritdoc />
    public (int Count, long Bytes) GetSummary()
    {
        List<string> paths;
        lock (_lock)
        {
            paths = LoadRecords().Saved.Values.ToList();
        }

        var files = paths.Where(File.Exists).Select(p => new FileInfo(p)).ToList();
        return (files.Count, files.Sum(f => f.Length));
    }

    /// <inheritdoc />
    public async Task<int> DeleteAllAsync(CancellationToken cancellationToken)
    {
        await _downloadLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Dictionary<string, string> saved;
            lock (_lock)
            {
                saved = new Dictionary<string, string>(LoadRecords().Saved);
            }

            var deleted = 0;
            foreach (var (itemId, path) in saved)
            {
                if (File.Exists(path))
                {
                    TryDelete(path);
                    deleted++;
                }

                // Remove the trailers folder too when nothing else is in it.
                var trailers = Path.GetDirectoryName(path);
                if (trailers is not null && Directory.Exists(trailers) && !Directory.EnumerateFileSystemEntries(trailers).Any())
                {
                    Directory.Delete(trailers);
                }

                if (Guid.TryParse(itemId, out var id) && _libraryManager.GetItemById(id) is { } item)
                {
                    Refresh(item);
                }

                lock (_lock)
                {
                    LoadRecords().Saved.Remove(itemId);
                    SaveRecords();
                }
            }

            _logger.LogInformation("Deleted {Count} saved trailers", deleted);
            return deleted;
        }
        finally
        {
            _downloadLock.Release();
        }
    }

    /// <inheritdoc />
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        _downloadManager.DownloadCompleted += OnDownloadCompleted;
        return base.StartAsync(cancellationToken);
    }

    /// <inheritdoc />
    public override Task StopAsync(CancellationToken cancellationToken)
    {
        _downloadManager.DownloadCompleted -= OnDownloadCompleted;
        return base.StopAsync(cancellationToken);
    }

    /// <inheritdoc />
    public override void Dispose()
    {
        _downloadLock.Dispose();
        base.Dispose();
    }

    /// <summary>
    /// Gets the folder a title's trailer goes in: a movie's own folder, or the series folder, once it's downloaded.
    /// </summary>
    /// <param name="item">The movie or series.</param>
    /// <returns>The folder, or <c>null</c> when the title isn't downloaded or has no folder of its own.</returns>
    internal string? GetTitleFolder(BaseItem item)
    {
        switch (item)
        {
            case Movie movie when !string.IsNullOrEmpty(movie.Path) && (!movie.IsInternet || movie.IsDownloaded) && File.Exists(movie.Path):
                var folder = Path.GetDirectoryName(movie.Path);

                // A movie straight in the library folder has no folder of its own.
                return folder is not null && !IsLibraryRoot(movie, folder) ? folder : null;

            case Series series when !string.IsNullOrEmpty(series.Path) && Directory.Exists(series.Path) && HasDownloadedEpisode(series):
                return series.Path;

            default:
                return null;
        }
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The earlier runtime cache kept trailers in the data folder; they're saved next to the media now.
        var oldCache = Path.Combine(DataFolder, "trailers");
        if (Directory.Exists(oldCache))
        {
            try
            {
                Directory.Delete(oldCache, true);
            }
            catch (IOException ex)
            {
                _logger.LogDebug(ex, "Couldn't remove the old trailer cache {Path}", oldCache);
            }
        }

        await foreach (var itemId in _queue.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                if (_libraryManager.GetItemById(itemId) is { } item)
                {
                    await SaveAsync(item, stoppingToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save the trailer of item {ItemId}", itemId);
            }
        }
    }

    private void OnDownloadCompleted(object? sender, BaseItem item) => Enqueue(item);

    private async Task<TrailerSaveResult> DownloadAsync(BaseItem item, string videoId, string folder, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(WorkFolder);
        var key = item.Id.ToString("N", CultureInfo.InvariantCulture);
        var download = Path.Combine(WorkFolder, key + ".download.mp4");
        var prepared = Path.Combine(WorkFolder, key + ".mp4");
        var ffmpeg = _mediaEncoder.EncoderPath;

        var args = new List<string>
        {
            "--no-playlist",
            "--no-progress",
            "--force-overwrites",
            "-f",
            _format,
            "--download-sections",
            string.Create(CultureInfo.InvariantCulture, $"*0-{Seconds}"),
            "--merge-output-format",
            "mp4",
            "-o",
            download
        };

        // A bare "ffmpeg" is found on the PATH by yt-dlp itself.
        if (!string.IsNullOrEmpty(ffmpeg) && Path.IsPathRooted(ffmpeg))
        {
            args.AddRange(["--ffmpeg-location", ffmpeg]);
        }

        args.Add("https://www.youtube.com/watch?v=" + videoId);

        try
        {
            var (exitCode, output) = await _ytDlp.RunAsync(args, _downloadTimeout, cancellationToken).ConfigureAwait(false);
            if (exitCode != 0 || !File.Exists(download))
            {
                var permanent = IsPermanentFailure(output);
                _logger.LogInformation(
                    "The trailer of {Title} wasn't saved ({Reason}): {Output}",
                    item.Name,
                    permanent ? "not available in 720p or better" : "will retry later",
                    LastLines(output));
                RememberAttempt(item.Id, videoId, permanent);
                return permanent ? TrailerSaveResult.Unavailable : TrailerSaveResult.Failed;
            }

            // Moves the index to the start, so a TV can start playing before the whole file has arrived.
            var remux = await YtDlp.RunProcessAsync(
                string.IsNullOrEmpty(ffmpeg) ? "ffmpeg" : ffmpeg,
                ["-v", "error", "-y", "-i", download, "-c", "copy", "-movflags", "+faststart", "-t", Seconds.ToString(CultureInfo.InvariantCulture), prepared],
                TimeSpan.FromMinutes(1),
                cancellationToken).ConfigureAwait(false);
            if (remux.ExitCode != 0)
            {
                _logger.LogWarning("Couldn't prepare the trailer of {Title}: {Output}", item.Name, LastLines(remux.Output));
                RememberAttempt(item.Id, videoId, false);
                return TrailerSaveResult.Failed;
            }

            var target = Path.Combine(folder, TrailersFolder, TrailerFile);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Move(prepared, target, true);
            lock (_lock)
            {
                LoadRecords().Saved[key] = target;
                LoadRecords().Attempts.Remove(key);
                SaveRecords();
            }

            _logger.LogInformation("Saved the trailer of {Title} to {Path}", item.Name, target);
            return TrailerSaveResult.Saved;
        }
        finally
        {
            TryDelete(download);
            TryDelete(prepared);
        }
    }

    private bool HasTrailer(BaseItem item, string folder)
    {
        var trailers = Path.Combine(folder, TrailersFolder);
        if (Directory.Exists(trailers) && Directory.EnumerateFiles(trailers).Any())
        {
            return true;
        }

        // Trailers added by hand, e.g. "Movie-trailer.mkv".
        return _libraryManager.GetItemList(new InternalItemsQuery { OwnerIds = [item.Id], ExtraTypes = [ExtraType.Trailer] }).Count > 0;
    }

    private bool HasDownloadedEpisode(Series series)
        => _libraryManager.GetItemList(new InternalItemsQuery
            {
                ParentId = series.Id,
                Recursive = true,
                IncludeItemTypes = [BaseItemKind.Episode],
                IsVirtualItem = false
            })
            .Any(e => !e.IsInternet || e.IsDownloaded);

    private bool IsLibraryRoot(BaseItem item, string folder)
        => _libraryManager.GetCollectionFolders(item)
            .OfType<CollectionFolder>()
            .SelectMany(f => f.PhysicalLocations)
            .Any(root => string.Equals(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase));

    private bool IsRecentlyAttempted(Guid itemId, string videoId)
    {
        lock (_lock)
        {
            return LoadRecords().Attempts.TryGetValue(itemId.ToString("N", CultureInfo.InvariantCulture), out var attempt)
                && string.Equals(attempt.VideoId, videoId, StringComparison.Ordinal)
                && DateTime.UtcNow - attempt.Utc < (attempt.Unavailable ? _retryUnavailable : _retryFailed);
        }
    }

    private void RememberAttempt(Guid itemId, string videoId, bool unavailable)
    {
        lock (_lock)
        {
            LoadRecords().Attempts[itemId.ToString("N", CultureInfo.InvariantCulture)] = new Attempt { VideoId = videoId, Utc = DateTime.UtcNow, Unavailable = unavailable };
            SaveRecords();
        }
    }

    // Jellyfin finds the new (or removed) trailer when it refreshes the title.
    private void Refresh(BaseItem item)
        => _providerManager.QueueRefresh(item.Id, new MetadataRefreshOptions(new DirectoryService(_fileSystem)), RefreshPriority.Normal);

    // Callers hold _lock.
    private Records LoadRecords()
    {
        if (_records is not null)
        {
            return _records;
        }

        try
        {
            _records = File.Exists(RecordsPath) ? JsonSerializer.Deserialize<Records>(File.ReadAllText(RecordsPath), _jsonOptions) : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            _logger.LogWarning(ex, "Couldn't read {Path}", RecordsPath);
        }

        _records ??= new Records();
        return _records;
    }

    // Callers hold _lock.
    private void SaveRecords()
    {
        Directory.CreateDirectory(DataFolder);
        var temp = RecordsPath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(LoadRecords(), _jsonOptions));
        File.Move(temp, RecordsPath, true);
    }

    private void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException ex)
        {
            _logger.LogDebug(ex, "Couldn't delete {Path}", path);
        }
    }

    private static string LastLines(string output)
        => string.Join(" | ", output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).TakeLast(3));

    [GeneratedRegex(@"(?:youtube(?:-nocookie)?\.com/(?:watch\?(?:.*&)?v=|embed/|shorts/|v/)|youtu\.be/)([\w-]{11})", RegexOptions.IgnoreCase)]
    private static partial Regex YouTubeIdRegex();

    private sealed class Records
    {
        public Dictionary<string, string> Saved { get; set; } = [];

        public Dictionary<string, Attempt> Attempts { get; set; } = [];
    }

    private sealed class Attempt
    {
        public string VideoId { get; set; } = string.Empty;

        public DateTime Utc { get; set; }

        public bool Unavailable { get; set; }
    }
}
