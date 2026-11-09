using System.Collections.Generic;

namespace MediaBrowser.Model.PirateFin;

/// <summary>
/// A page of a channel's videos, newest first.
/// </summary>
public sealed class YouTubeChannelVideos
{
    /// <summary>
    /// Gets or sets the videos.
    /// </summary>
    public IReadOnlyList<YouTubeVideo> Items { get; set; } = [];

    /// <summary>
    /// Gets or sets the token for the next page; <c>null</c> on the last one.
    /// </summary>
    public string? NextPageToken { get; set; }
}
