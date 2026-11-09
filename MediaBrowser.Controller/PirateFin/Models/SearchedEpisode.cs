using System;

namespace MediaBrowser.Controller.PirateFin.Models;

/// <summary>
/// An episode of a <see cref="SearchedSeason"/>.
/// </summary>
public class SearchedEpisode
{
    /// <summary>
    /// Gets or sets the episode number.
    /// </summary>
    public int Episode { get; set; }

    /// <summary>
    /// Gets or sets the episode title.
    /// </summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the overview.
    /// </summary>
    public string Overview { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets when the episode aired / airs, in UTC.
    /// </summary>
    public DateTime? FirstAiring { get; set; }

    /// <summary>
    /// Gets or sets the episode's own image (a still), or empty.
    /// </summary>
    public string ImageUrl { get; set; } = string.Empty;
}
