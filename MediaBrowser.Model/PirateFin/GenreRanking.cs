using System.Collections.Generic;

namespace MediaBrowser.Model.PirateFin;

/// <summary>
/// A user's genres in one library, most watched first, and genres to suggest to them.
/// </summary>
public sealed class GenreRanking
{
    /// <summary>
    /// Gets or sets the library: Shows, Anime or Movies.
    /// </summary>
    public string Library { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the genres the user watched, most titles first.
    /// </summary>
    public IReadOnlyList<RankedGenre> Genres { get; set; } = [];

    /// <summary>
    /// Gets or sets genres the other users watch a lot that this user rarely or never watches, most popular first.
    /// Empty when no one else has watched enough.
    /// </summary>
    public IReadOnlyList<RankedGenre> Discovery { get; set; } = [];
}
