using System.Collections.Generic;

namespace MediaBrowser.Controller.PirateFin.Models;

/// <summary>
/// A season of a <see cref="SearchedSeries"/>.
/// </summary>
public class SearchedSeason
{
    /// <summary>
    /// Gets or sets the season number.
    /// </summary>
    public int Season { get; set; } = 1;

    /// <summary>
    /// Gets or sets the folder / display title, e.g. "Season 1".
    /// </summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the season's own title (anime seasons are separate AniList entries).
    /// </summary>
    public string AlternateTitle { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the romaji title of the season.
    /// </summary>
    public string RomajiTitle { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the AniList id of the season (anime), or empty.
    /// </summary>
    public string AniListId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether AniList has more scheduled episodes than the search returned.
    /// </summary>
    public bool HasMoreEpisodes { get; set; }

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
    /// Gets or sets the episodes.
    /// </summary>
    public List<SearchedEpisode> Episodes { get; set; } = [];
}
