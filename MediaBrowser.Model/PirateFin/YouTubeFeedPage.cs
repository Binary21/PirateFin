using System.Collections.Generic;

namespace MediaBrowser.Model.PirateFin;

/// <summary>
/// A page of the YouTube home grid.
/// </summary>
public sealed class YouTubeFeedPage
{
    /// <summary>
    /// Gets or sets the videos.
    /// </summary>
    public IReadOnlyList<YouTubeVideo> Items { get; set; } = [];

    /// <summary>
    /// Gets or sets a value indicating whether there are more.
    /// </summary>
    public bool HasMore { get; set; }
}
