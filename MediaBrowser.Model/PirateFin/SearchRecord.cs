using System;

namespace MediaBrowser.Model.PirateFin;

/// <summary>
/// A search a user made for a show, anime or movie to add.
/// </summary>
public sealed class SearchRecord
{
    /// <summary>
    /// Gets or sets the user id.
    /// </summary>
    public Guid UserId { get; set; }

    /// <summary>
    /// Gets or sets the library searched: Shows, Anime or Movies.
    /// </summary>
    public string Library { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the search text.
    /// </summary>
    public string Query { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the number of results found.
    /// </summary>
    public int Results { get; set; }

    /// <summary>
    /// Gets or sets when the search was made.
    /// </summary>
    public DateTime Utc { get; set; }
}
