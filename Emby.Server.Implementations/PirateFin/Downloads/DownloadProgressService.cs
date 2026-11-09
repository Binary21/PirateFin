using System;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.PirateFin;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.PirateFin;

namespace Emby.Server.Implementations.PirateFin.Downloads;

/// <summary>
/// Default <see cref="IDownloadProgress"/>.
/// </summary>
public sealed class DownloadProgressService : IDownloadProgress
{
    private readonly IDownloadManager _downloadManager;
    private readonly IPirateFinConfigurationStore _configurationStore;
    private readonly IFileSystem _fileSystem;
    private readonly PlayableTimeEstimator _playableTime;

    /// <summary>
    /// Initializes a new instance of the <see cref="DownloadProgressService"/> class.
    /// </summary>
    /// <param name="downloadManager">The download manager.</param>
    /// <param name="configurationStore">The PirateFin settings (the playable progress).</param>
    /// <param name="fileSystem">The file system.</param>
    /// <param name="playableTime">Works out how far a downloading video plays without a gap.</param>
    public DownloadProgressService(IDownloadManager downloadManager, IPirateFinConfigurationStore configurationStore, IFileSystem fileSystem, PlayableTimeEstimator playableTime)
    {
        _downloadManager = downloadManager;
        _configurationStore = configurationStore;
        _fileSystem = fileSystem;
        _playableTime = playableTime;
    }

    /// <inheritdoc />
    public bool IsPlayable(Video video)
    {
        if (!video.IsInternet || video.IsDownloaded)
        {
            return true;
        }

        if (!video.IsFileProtocol || string.IsNullOrWhiteSpace(video.Path))
        {
            return false;
        }

        var file = _fileSystem.GetFileSystemInfo(video.Path);
        return file.Exists && !file.IsDirectory && file.Length > 0 && (video.IsPlayable || video.RunTimeTicks > 0);
    }

    /// <inheritdoc />
    public DownloadProgressInfo Get(Video video)
    {
        var playable = IsPlayable(video);
        var downloaded = !video.IsInternet || video.IsDownloaded;
        var progress = video.IsDownloaded ? 1 : _downloadManager.GetProgress(video.Id);
        var contiguous = video.IsDownloaded ? 1 : _downloadManager.GetContiguousProgress(video.Id);
        var threshold = _configurationStore.GetQBitTorrent().PlayableProgress;
        double? playableSeconds = null;
        if (downloaded)
        {
            _playableTime.Forget(video.Id);
        }
        else if (playable && contiguous is { } c && video.RunTimeTicks is > 0 and { } ticks && !string.IsNullOrEmpty(video.Path))
        {
            playableSeconds = Math.Round(_playableTime.GetPlayableSeconds(video.Id, video.Path, c, TimeSpan.FromTicks(ticks).TotalSeconds), 1);
        }

        return new DownloadProgressInfo
        {
            ItemId = video.Id,
            Playable = playable,
            Downloaded = downloaded,
            State = video.IsDownloaded ? DownloadState.None : _downloadManager.GetState(video.Id),
            Progress = progress,
            ContiguousProgress = contiguous,
            PlayableSeconds = playableSeconds,
            PlayableProgress = threshold,

            // The Play button fills toward the same gap-free amount that makes it playable.
            PlayableFill = playable ? 1 : Math.Clamp((contiguous ?? 0) / (threshold > 0 ? threshold : 1), 0, 1),
            Slow = !playable && _downloadManager.IsSlow(video.Id)
        };
    }
}
