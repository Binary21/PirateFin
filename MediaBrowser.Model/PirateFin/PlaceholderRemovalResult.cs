namespace MediaBrowser.Model.PirateFin;

/// <summary>
/// What a library reset removed.
/// </summary>
public sealed class PlaceholderRemovalResult
{
    /// <summary>
    /// Gets or sets the number of removed movies.
    /// </summary>
    public int Movies { get; set; }

    /// <summary>
    /// Gets or sets the number of removed episodes.
    /// </summary>
    public int Episodes { get; set; }

    /// <summary>
    /// Gets or sets the number of removed seasons.
    /// </summary>
    public int Seasons { get; set; }

    /// <summary>
    /// Gets or sets the number of removed series.
    /// </summary>
    public int Series { get; set; }
}
