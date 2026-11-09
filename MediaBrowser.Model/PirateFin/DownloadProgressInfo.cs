using System;

namespace MediaBrowser.Model.PirateFin;

/// <summary>
/// An item's download status for its Play button: fetched once over HTTP, then pushed over the WebSocket to the
/// connections following the item.
/// </summary>
public class DownloadProgressInfo
{
    /// <summary>
    /// Gets or sets the item id.
    /// </summary>
    public Guid ItemId { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether enough is on disk to start playing (season / series: any episode).
    /// </summary>
    public bool Playable { get; set; }

    /// <summary>
    /// Gets or sets where the download stands.
    /// </summary>
    public DownloadState State { get; set; }

    /// <summary>
    /// Gets or sets the download progress, between 0 and 1; <c>null</c> when the item isn't downloading.
    /// </summary>
    public double? Progress { get; set; }

    /// <summary>
    /// Gets or sets how much of the video is downloaded without a gap from its start, between 0 and 1; <c>null</c>
    /// when the item isn't downloading.
    /// </summary>
    public double? ContiguousProgress { get; set; }

    /// <summary>
    /// Gets or sets how far into the video, in seconds, the data reaches without a gap (the last keyframe before the
    /// downloaded edge, found through the file's index): a player going past it would read pieces that aren't
    /// downloaded yet. <c>null</c> when downloaded, not playable yet or the runtime is unknown.
    /// </summary>
    public double? PlayableSeconds { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the whole file is on disk (always for items PirateFin didn't download).
    /// </summary>
    public bool Downloaded { get; set; }

    /// <summary>
    /// Gets or sets the progress (0-1) at which a download becomes playable.
    /// </summary>
    public double PlayableProgress { get; set; }

    /// <summary>
    /// Gets or sets how far the download is towards being playable, between 0 and 1 (1 = playable): how much of the
    /// Play button is filled. 5% downloaded with playable at 10% is half.
    /// </summary>
    public double PlayableFill { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the download is slow (expected to take more than an hour): the page
    /// tells the user to come back later.
    /// </summary>
    public bool Slow { get; set; }
}
