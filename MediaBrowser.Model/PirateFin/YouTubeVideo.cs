using System;

namespace MediaBrowser.Model.PirateFin;

/// <summary>
/// A YouTube video as the YouTube tab shows it.
/// </summary>
public sealed class YouTubeVideo
{
    /// <summary>
    /// Gets or sets the YouTube video id.
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the title.
    /// </summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the description.
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the channel id.
    /// </summary>
    public string ChannelId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the channel name.
    /// </summary>
    public string ChannelTitle { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the channel's avatar image.
    /// </summary>
    public string? ChannelAvatarUrl { get; set; }

    /// <summary>
    /// Gets or sets the thumbnail image (16:9).
    /// </summary>
    public string? ThumbnailUrl { get; set; }

    /// <summary>
    /// Gets or sets when it was published.
    /// </summary>
    public DateTime PublishedUtc { get; set; }

    /// <summary>
    /// Gets or sets its length in seconds.
    /// </summary>
    public int DurationSeconds { get; set; }

    /// <summary>
    /// Gets or sets its view count.
    /// </summary>
    public long? ViewCount { get; set; }

    /// <summary>
    /// Gets or sets its YouTube category.
    /// </summary>
    public string? CategoryId { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether YouTube marks it made for kids.
    /// </summary>
    public bool MadeForKids { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether it's a live stream or premiere (left out of the tab).
    /// </summary>
    public bool IsLive { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether it's from a channel the user subscribes to (otherwise a recommendation).
    /// </summary>
    public bool FromSubscription { get; set; }

    /// <summary>
    /// Gets or sets how far the user got.
    /// </summary>
    public double PositionSeconds { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the user watched it to the end.
    /// </summary>
    public bool Watched { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the user downloaded it.
    /// </summary>
    public bool Downloaded { get; set; }
}
