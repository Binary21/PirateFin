using System;
using System.Collections.Generic;

namespace MediaBrowser.Model.PirateFin;

/// <summary>
/// A series, movie or episode in one of the insight lists.
/// </summary>
public sealed class InsightTitle
{
    /// <summary>
    /// Gets or sets the item id.
    /// </summary>
    public Guid ItemId { get; set; }

    /// <summary>
    /// Gets or sets the name (for an episode: "Series S1:E3").
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the type: Series, Movie or Episode.
    /// </summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the library name.
    /// </summary>
    public string? Library { get; set; }

    /// <summary>
    /// Gets or sets the genres.
    /// </summary>
    public IReadOnlyList<string> Genres { get; set; } = [];

    /// <summary>
    /// Gets or sets how far the user got, e.g. "4 of 10 episodes" or "35%".
    /// </summary>
    public string? Progress { get; set; }

    /// <summary>
    /// Gets or sets how often it was played (rewatches).
    /// </summary>
    public int PlayCount { get; set; }

    /// <summary>
    /// Gets or sets when the user last played it.
    /// </summary>
    public DateTime? LastPlayedUtc { get; set; }
}
