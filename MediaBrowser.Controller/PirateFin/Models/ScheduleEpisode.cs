using System;

namespace MediaBrowser.Controller.PirateFin.Models;

/// <summary>
/// An episode airing this week, as reported by Trakt or AniList.
/// </summary>
public class ScheduleEpisode
{
    /// <summary>
    /// Gets or sets the series title.
    /// </summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the alternate (romaji) series title.
    /// </summary>
    public string AlternateTitle { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the season number.
    /// </summary>
    public int Season { get; set; } = 1;

    /// <summary>
    /// Gets or sets the episode number.
    /// </summary>
    public int? Number { get; set; }

    /// <summary>
    /// Gets or sets the episode title.
    /// </summary>
    public string EpisodeTitle { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the overview.
    /// </summary>
    public string Overview { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets when the episode airs, in UTC.
    /// </summary>
    public DateTime? FirstAiring { get; set; }

    /// <summary>
    /// Gets or sets the poster url.
    /// </summary>
    public string ImageUrl { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a known magnet url.
    /// </summary>
    public string MagnetUrl { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether the season should be subscribed on creation.
    /// </summary>
    public bool Subscribed { get; set; }

    /// <summary>
    /// Gets or sets the AniList airing status of the series.
    /// </summary>
    public string AiringStatus { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the genres.
    /// </summary>
    public string[] Genres { get; set; } = [];
}
