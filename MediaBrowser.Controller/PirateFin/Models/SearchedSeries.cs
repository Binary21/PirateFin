using System.Collections.Generic;

namespace MediaBrowser.Controller.PirateFin.Models;

/// <summary>
/// A full series (all seasons and episodes) fetched from Trakt or AniList.
/// </summary>
public class SearchedSeries
{
    /// <summary>
    /// Gets or sets the title.
    /// </summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the alternate title (Trakt slug or romaji title).
    /// </summary>
    public string AlternateTitle { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the year the series started.
    /// </summary>
    public int? Year { get; set; }

    /// <summary>
    /// Gets or sets the overview.
    /// </summary>
    public string Overview { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the poster url.
    /// </summary>
    public string ImageUrl { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the genres.
    /// </summary>
    public string[] Genres { get; set; } = [];

    /// <summary>
    /// Gets or sets the AniList id of the first season, if any.
    /// </summary>
    public string AniListId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the seasons.
    /// </summary>
    public List<SearchedSeason> Seasons { get; set; } = [];
}
