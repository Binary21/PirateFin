using System;

namespace MediaBrowser.Model.PirateFin;

/// <summary>
/// A show with episodes still to search for releases.
/// </summary>
public class ReleaseSearchShow
{
    /// <summary>
    /// Gets or sets the series id.
    /// </summary>
    public Guid SeriesId { get; set; }

    /// <summary>
    /// Gets or sets the series name.
    /// </summary>
    public string SeriesName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets how many of its episodes that aired in the last week are still being searched.
    /// </summary>
    public int NewEpisodes { get; set; }

    /// <summary>
    /// Gets or sets how many of its older episodes wait for their one search.
    /// </summary>
    public int OldEpisodes { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether its old searches are paused on their own (not just with every show's).
    /// </summary>
    public bool Paused { get; set; }

    /// <summary>
    /// Gets or sets when its next search runs, in UTC (in the past: due, waiting for a free slot); <c>null</c> when
    /// all that's left of it is paused.
    /// </summary>
    public DateTime? NextSearchUtc { get; set; }
}
