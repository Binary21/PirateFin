using System;

namespace MediaBrowser.Model.PirateFin;

/// <summary>
/// A genre and how much a user (or the other users) watch it.
/// </summary>
public sealed class RankedGenre
{
    /// <summary>
    /// Gets or sets the genre id.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Gets or sets the genre name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the number of titles watched in this genre: movies watched at least halfway and series with at
    /// least three episodes played. For a discovery genre, the total over the other users.
    /// </summary>
    public int Titles { get; set; }
}
