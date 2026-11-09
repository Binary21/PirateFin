using System.Collections.Generic;

namespace MediaBrowser.Model.PirateFin;

/// <summary>
/// A video's watch page.
/// </summary>
public sealed class YouTubeVideoDetails
{
    /// <summary>
    /// Gets or sets the video.
    /// </summary>
    public YouTubeVideo Video { get; set; } = new();

    /// <summary>
    /// Gets or sets its channel.
    /// </summary>
    public YouTubeChannel Channel { get; set; } = new();

    /// <summary>
    /// Gets or sets what to watch next.
    /// </summary>
    public IReadOnlyList<YouTubeVideo> UpNext { get; set; } = [];
}
