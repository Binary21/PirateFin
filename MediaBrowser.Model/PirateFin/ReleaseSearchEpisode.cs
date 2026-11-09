namespace MediaBrowser.Model.PirateFin;

/// <summary>
/// An episode being searched for a release.
/// </summary>
public class ReleaseSearchEpisode
{
    /// <summary>
    /// Gets or sets the series name.
    /// </summary>
    public string SeriesName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the episode, e.g. "S2E5".
    /// </summary>
    public string Episode { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the episode title.
    /// </summary>
    public string Name { get; set; } = string.Empty;
}
