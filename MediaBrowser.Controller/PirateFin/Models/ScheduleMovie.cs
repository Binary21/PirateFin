using System;

namespace MediaBrowser.Controller.PirateFin.Models;

/// <summary>
/// A newly released movie, as reported by YTS.
/// </summary>
public class ScheduleMovie
{
    /// <summary>
    /// Gets or sets the title.
    /// </summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the release year.
    /// </summary>
    public int Year { get; set; }

    /// <summary>
    /// Gets or sets the IMDb id (e.g. tt1234567), which lets the metadata providers find the exact movie.
    /// </summary>
    public string ImdbId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the overview.
    /// </summary>
    public string Overview { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets when the movie was released on YTS, in UTC.
    /// </summary>
    public DateTime? FirstAiring { get; set; }

    /// <summary>
    /// Gets or sets the poster url.
    /// </summary>
    public string ImageUrl { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the magnet url.
    /// </summary>
    public string MagnetUrl { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the genres.
    /// </summary>
    public string[] Genres { get; set; } = [];

    /// <summary>
    /// Gets the file name (without extension) used for the placeholder, e.g. "Title 2026".
    /// </summary>
    public string FileTitle => Year > 0 ? $"{Title} {Year}" : Title;
}
