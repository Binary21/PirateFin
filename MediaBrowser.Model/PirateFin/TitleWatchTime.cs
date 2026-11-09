using System;

namespace MediaBrowser.Model.PirateFin;

/// <summary>
/// A series or movie and how much the user watched it.
/// </summary>
public sealed class TitleWatchTime
{
    /// <summary>
    /// Gets or sets the series or movie id.
    /// </summary>
    public Guid ItemId { get; set; }

    /// <summary>
    /// Gets or sets the name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the type: Series or Movie.
    /// </summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the library name.
    /// </summary>
    public string? Library { get; set; }

    /// <summary>
    /// Gets or sets the seconds watched.
    /// </summary>
    public long Seconds { get; set; }

    /// <summary>
    /// Gets or sets the number of different episodes watched (0 for a movie).
    /// </summary>
    public int Episodes { get; set; }

    /// <summary>
    /// Gets or sets when the user last watched it.
    /// </summary>
    public DateTime LastWatchedUtc { get; set; }
}
