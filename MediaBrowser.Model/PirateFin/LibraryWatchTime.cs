namespace MediaBrowser.Model.PirateFin;

/// <summary>
/// Seconds watched in one library.
/// </summary>
public sealed class LibraryWatchTime
{
    /// <summary>
    /// Gets or sets the library name.
    /// </summary>
    public string Library { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the seconds watched.
    /// </summary>
    public long Seconds { get; set; }
}
