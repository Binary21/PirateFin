using System;

namespace MediaBrowser.Model.PirateFin;

/// <summary>
/// A lightweight series found on Trakt / AniList. Nothing is created in the library until the result is opened.
/// </summary>
public sealed class InternetSearchResult
{
    /// <summary>
    /// Gets or sets the provider key used to open the result (Trakt slug or AniList id).
    /// </summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the PirateFin library the result belongs to (Shows or Anime).
    /// </summary>
    public string Library { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the title.
    /// </summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the year the series started, if known.
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
    /// Gets or sets the id of the library series when it has already been created.
    /// </summary>
    public Guid? ItemId { get; set; }
}
