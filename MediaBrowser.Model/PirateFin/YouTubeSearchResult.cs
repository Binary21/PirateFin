using System.Collections.Generic;

namespace MediaBrowser.Model.PirateFin;

/// <summary>
/// YouTube search results.
/// </summary>
public sealed class YouTubeSearchResult
{
    /// <summary>
    /// Gets or sets the account whose name or @handle matches the search, shown first.
    /// </summary>
    public YouTubeChannel? Channel { get; set; }

    /// <summary>
    /// Gets or sets the videos, newest first.
    /// </summary>
    public IReadOnlyList<YouTubeVideo> Items { get; set; } = [];

    /// <summary>
    /// Gets or sets the searches left today across all users.
    /// </summary>
    public int SearchesLeft { get; set; }

    /// <summary>
    /// Gets or sets why there are no results (e.g. the daily limit was reached).
    /// </summary>
    public string? Message { get; set; }
}
